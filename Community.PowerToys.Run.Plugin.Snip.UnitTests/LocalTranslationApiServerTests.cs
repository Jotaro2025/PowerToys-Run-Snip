// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class LocalTranslationApiServerTests
{
    private const string Token = "test-token-123";

    private sealed class Reply
    {
        public int Status { get; init; }

        public string Body { get; init; }

        public Dictionary<string, string> Headers { get; init; }
    }

    private static LocalTranslationApiServer Start(
        Func<IReadOnlyList<string>, string, CancellationToken, Task<IReadOnlyList<string>>> translate,
        IReadOnlyList<string> origins = null,
        int rateLimit = LocalTranslationApiServer.DefaultMaxRequestsPerMinute)
    {
        var server = new LocalTranslationApiServer(0, Token, origins ?? [], translate, rateLimit);
        server.Start();
        Assert.IsTrue(server.IsRunning, server.LastError);
        Assert.IsTrue(server.Port > 0);
        return server;
    }

    private static Func<IReadOnlyList<string>, string, CancellationToken, Task<IReadOnlyList<string>>> Echo(
        List<(string[] Texts, string Target)> calls = null)
        => (texts, target, _) =>
        {
            if (calls != null)
            {
                lock (calls)
                {
                    calls.Add((texts.ToArray(), target));
                }
            }

            return Task.FromResult<IReadOnlyList<string>>(texts.Select(t => "T:" + t).ToArray());
        };

    private static async Task<Reply> SendAsync(
        LocalTranslationApiServer server,
        string method,
        string path,
        string body = null,
        bool authorized = true,
        string origin = null,
        string host = null,
        string contentType = "application/json")
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using var stream = client.GetStream();

        var request = new StringBuilder()
            .Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(host ?? $"127.0.0.1:{server.Port}").Append("\r\n")
            .Append("Connection: close\r\n");

        if (authorized)
        {
            request.Append("Authorization: Bearer ").Append(Token).Append("\r\n");
        }

        if (origin != null)
        {
            request.Append("Origin: ").Append(origin).Append("\r\n");
        }

        byte[] bodyBytes = body == null ? [] : Encoding.UTF8.GetBytes(body);
        if (body != null)
        {
            request.Append("Content-Type: ").Append(contentType).Append("\r\n");
            request.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        }

        request.Append("\r\n");
        byte[] headBytes = Encoding.ASCII.GetBytes(request.ToString());
        await stream.WriteAsync(headBytes);
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
        string raw = await reader.ReadToEndAsync(timeout.Token);
        return Parse(raw);
    }

    private static Reply Parse(string raw)
    {
        int split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.IsTrue(split > 0, "missing header terminator: " + raw);
        string head = raw[..split];
        string body = raw[(split + 4)..];
        string[] lines = head.Split("\r\n");
        string[] status = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        return new Reply
        {
            Status = int.Parse(status[1], System.Globalization.CultureInfo.InvariantCulture),
            Body = body,
            Headers = headers,
        };
    }

    private static string TranslateJson(params string[] texts)
        => JsonSerializer.Serialize(new { texts, target = "English" });

    [TestMethod]
    public async Task HealthRequiresToken()
    {
        using var server = Start(Echo());

        var unauthorized = await SendAsync(server, "GET", "/v1/health", authorized: false);
        Assert.AreEqual(401, unauthorized.Status);

        var health = await SendAsync(server, "GET", "/v1/health");
        Assert.AreEqual(200, health.Status);
        StringAssert.Contains(health.Body, "\"status\":\"ok\"");
        Assert.AreEqual("*", health.Headers["Access-Control-Allow-Origin"]);
    }

    [TestMethod]
    public async Task TranslatePreservesOrderAndTarget()
    {
        var calls = new List<(string[] Texts, string Target)>();
        using var server = Start(Echo(calls));

        var reply = await SendAsync(server, "POST", "/v1/translate", TranslateJson("alpha", "beta"));

        Assert.AreEqual(200, reply.Status, reply.Body);
        StringAssert.Contains(reply.Body, "\"translations\"");
        StringAssert.Contains(reply.Body, "T:alpha");
        StringAssert.Contains(reply.Body, "T:beta");
        Assert.HasCount(1, calls);
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, calls[0].Texts);
        Assert.AreEqual("English", calls[0].Target);
    }

    [TestMethod]
    public async Task RejectsForeignHost()
    {
        using var server = Start(Echo());

        var reply = await SendAsync(server, "GET", "/v1/health", host: "evil.example:17853");

        Assert.AreEqual(400, reply.Status);
    }

    [TestMethod]
    public async Task OriginAllowlistControlsCorsAndPreflight()
    {
        using var server = Start(Echo(), ["chrome-extension://abc"]);

        var allowed = await SendAsync(server, "OPTIONS", "/v1/translate", origin: "chrome-extension://abc");
        Assert.AreEqual(204, allowed.Status);
        Assert.AreEqual("chrome-extension://abc", allowed.Headers["Access-Control-Allow-Origin"]);
        StringAssert.Contains(allowed.Headers["Access-Control-Allow-Headers"], "authorization");

        var denied = await SendAsync(server, "OPTIONS", "/v1/translate", origin: "https://evil.example");
        Assert.AreEqual(403, denied.Status);

        var translated = await SendAsync(server, "POST", "/v1/translate", TranslateJson("x"), origin: "chrome-extension://abc");
        Assert.AreEqual(200, translated.Status);
        Assert.AreEqual("chrome-extension://abc", translated.Headers["Access-Control-Allow-Origin"]);
    }

    [TestMethod]
    public async Task RejectsTooManyTexts()
    {
        using var server = Start(Echo());
        string[] texts = Enumerable.Range(0, LocalTranslationApiServer.MaxTexts + 1).Select(i => "t" + i).ToArray();

        var reply = await SendAsync(server, "POST", "/v1/translate", TranslateJson(texts));

        Assert.AreEqual(413, reply.Status);
    }

    [TestMethod]
    public async Task UnavailableEngineMapsTo503()
    {
        using var server = Start((_, _, _) => throw new LocalApiUnavailableException("provider off"));

        var reply = await SendAsync(server, "POST", "/v1/translate", TranslateJson("x"));

        Assert.AreEqual(503, reply.Status);
        StringAssert.Contains(reply.Body, "provider off");
    }

    [TestMethod]
    public async Task RateLimitReturns429()
    {
        using var server = Start(Echo(), rateLimit: 2);

        Assert.AreEqual(200, (await SendAsync(server, "POST", "/v1/translate", TranslateJson("a"))).Status);
        Assert.AreEqual(200, (await SendAsync(server, "POST", "/v1/translate", TranslateJson("b"))).Status);
        Assert.AreEqual(429, (await SendAsync(server, "POST", "/v1/translate", TranslateJson("c"))).Status);
    }

    [TestMethod]
    public async Task RejectsMissingBodyFields()
    {
        using var server = Start(Echo());

        var reply = await SendAsync(server, "POST", "/v1/translate", "{\"target\":\"English\"}");

        Assert.AreEqual(400, reply.Status);
    }

    [TestMethod]
    public void ParseOriginsSplitsCommaAndSemicolon()
    {
        CollectionAssert.AreEqual(
            new[] { "chrome-extension://a", "moz-extension://b" },
            LocalTranslationApi.ParseOrigins("chrome-extension://a, moz-extension://b;").ToArray());
        Assert.IsEmpty(LocalTranslationApi.ParseOrigins(null));
        Assert.IsEmpty(LocalTranslationApi.ParseOrigins("   "));
    }

    [TestMethod]
    public void GenerateTokenIsRandomAndStableLength()
    {
        string a = LocalTranslationApi.GenerateToken();
        string b = LocalTranslationApi.GenerateToken();
        Assert.AreEqual(48, a.Length);
        Assert.AreEqual(48, b.Length);
        Assert.AreNotEqual(a, b);
        Assert.IsTrue(a.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }
}
