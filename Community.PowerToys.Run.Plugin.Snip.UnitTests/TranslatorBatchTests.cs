// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class TranslatorBatchTests
{
    [TestMethod]
    public void BuildNumberedPromptIncludesAllLines()
    {
        var prompt = TranslatorBatch.BuildNumberedPrompt("English", new[] { "你好", "世界" });
        StringAssert.Contains(prompt, "English");
        StringAssert.Contains(prompt, "1. 你好");
        StringAssert.Contains(prompt, "2. 世界");
    }

    [TestMethod]
    public void TryParseNumberedReplyAlignsByIndex()
    {
        const string reply = """
            1. Hello
            2. World
            """;
        Assert.IsTrue(TranslatorBatch.TryParseNumberedReply(reply, 2, out var parsed));
        CollectionAssert.AreEqual(new[] { "Hello", "World" }, parsed);
    }

    [TestMethod]
    public void TryParseNumberedReplyAcceptsAlternateSeparators()
    {
        const string reply = """
            1) Alpha
            2、Beta
            3: Gamma
            """;
        Assert.IsTrue(TranslatorBatch.TryParseNumberedReply(reply, 3, out var parsed));
        CollectionAssert.AreEqual(new[] { "Alpha", "Beta", "Gamma" }, parsed);
    }

    [TestMethod]
    public void TryParseNumberedReplyFailsWhenMissingLine()
    {
        Assert.IsFalse(TranslatorBatch.TryParseNumberedReply("1. Only one", 2, out _));
    }

    [TestMethod]
    public void TryParseNumberedReplyFailsOnEmpty()
    {
        Assert.IsFalse(TranslatorBatch.TryParseNumberedReply("   ", 1, out _));
        Assert.IsFalse(TranslatorBatch.TryParseNumberedReply(null, 1, out _));
    }

    [TestMethod]
    public void ChunkRespectsMaxLinesAndChars()
    {
        var items = Enumerable.Range(0, 5).Select(i => (i, Text: new string('a', 10))).ToList();
        var byLines = TranslatorBatch.Chunk(items, maxLines: 2, maxChars: 10_000);
        Assert.AreEqual(3, byLines.Count);
        Assert.AreEqual(2, byLines[0].Count);
        Assert.AreEqual(2, byLines[1].Count);
        Assert.AreEqual(1, byLines[2].Count);

        var byChars = TranslatorBatch.Chunk(items, maxLines: 100, maxChars: 25);
        Assert.IsTrue(byChars.Count >= 2);
        Assert.IsTrue(byChars.All(c => c.Sum(x => x.Text.Length) <= 25 || c.Count == 1));
    }

    [TestMethod]
    public void CollectNonEmptySkipsBlanks()
    {
        var work = TranslatorBatch.CollectNonEmpty(new[] { "a", " ", null, "b" });
        Assert.AreEqual(2, work.Count);
        Assert.AreEqual(0, work[0].Index);
        Assert.AreEqual(3, work[1].Index);
    }

    [TestMethod]
    public async Task HttpTranslatorBatchesMultipleLinesInOneRequest()
    {
        int postCount = 0;
        var handler = new ScriptableHandler((request, ct) =>
        {
            Interlocked.Increment(ref postCount);
            Assert.AreEqual(HttpMethod.Post, request.Method);
            var body = request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            StringAssert.Contains(body, "1. Hello");
            StringAssert.Contains(body, "2. World");
            const string payload = """
                {"choices":[{"message":{"content":"1. 你好\n2. 世界"}}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9/v1/") };
        using var translator = new HttpOpenAiTranslator("http://127.0.0.1:9/v1", apiKey: null, model: "test", http);
        var results = await translator.TranslateAsync(new[] { "Hello", "World" }, "简体中文");
        CollectionAssert.AreEqual(new[] { "你好", "世界" }, results.ToArray());
        Assert.AreEqual(1, postCount, "expected a single batched chat/completions call");
    }

    [TestMethod]
    public async Task HttpTranslatorFallsBackToPerLineWhenParseFails()
    {
        int postCount = 0;
        var handler = new ScriptableHandler((request, ct) =>
        {
            int n = Interlocked.Increment(ref postCount);
            var body = request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            string content;
            if (n == 1)
            {
                // Unparseable batch reply.
                content = "Sorry I cannot number these.";
            }
            else if (body.Contains("Hello", StringComparison.Ordinal))
            {
                content = "你好";
            }
            else
            {
                content = "世界";
            }

            var payload = "{\"choices\":[{\"message\":{\"content\":\"" + content + "\"}}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9/v1/") };
        using var translator = new HttpOpenAiTranslator("http://127.0.0.1:9/v1", apiKey: null, model: "test", http);
        var results = await translator.TranslateAsync(new[] { "Hello", "World" }, "简体中文");
        CollectionAssert.AreEqual(new[] { "你好", "世界" }, results.ToArray());
        Assert.AreEqual(3, postCount, "1 failed batch + 2 per-line fallbacks");
    }

    [TestMethod]
    public async Task HttpTranslatorPreservesEmptySlots()
    {
        int postCount = 0;
        var handler = new ScriptableHandler((_, _) =>
        {
            Interlocked.Increment(ref postCount);
            const string payload = """
                {"choices":[{"message":{"content":"1. Bonjour\n2. Monde"}}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9/v1/") };
        using var translator = new HttpOpenAiTranslator("http://127.0.0.1:9/v1", apiKey: null, model: "test", http);
        var results = await translator.TranslateAsync(new[] { "Hello", "  ", "World" }, "French");
        CollectionAssert.AreEqual(new[] { "Bonjour", string.Empty, "Monde" }, results.ToArray());
        Assert.AreEqual(1, postCount);
    }


    [TestMethod]
    public void FormatBilingualTextInterleavesSourceAndTranslation()
    {
        var text = TranslatorBatch.FormatBilingualText(
            new[] { "Hello", "World" },
            new[] { "你好", "世界" });
        string expected = "Hello" + Environment.NewLine + "你好" + Environment.NewLine + Environment.NewLine + "World" + Environment.NewLine + "世界";
        Assert.AreEqual(expected, text);
    }

    [TestMethod]
    public async Task HttpTranslatorPartialFailureKeepsSourceTextAndRecordsWarning()
    {
        _ = TranslatorBatch.ConsumePartialFallbackWarning();
        int postCount = 0;
        var handler = new ScriptableHandler((request, ct) =>
        {
            int n = Interlocked.Increment(ref postCount);
            var body = request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            if (n == 1)
            {
                // Force per-line fallback.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{invalid json"),
                });
            }

            if (body.Contains("Hello", StringComparison.Ordinal))
            {
                const string payload = """{"choices":[{"message":{"content":"你好"}}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{invalid json"),
            });
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9/v1/") };
        using var translator = new HttpOpenAiTranslator("http://127.0.0.1:9/v1", apiKey: null, model: "test", http);
        var results = await translator.TranslateAsync(new[] { "Hello", "World" }, "简体中文");
        CollectionAssert.AreEqual(new[] { "你好", "World" }, results.ToArray());
        Assert.AreEqual(1, translator.LastPartialFallbackCount);
        Assert.IsFalse(string.IsNullOrEmpty(TranslatorBatch.ConsumePartialFallbackWarning()));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public async Task HttpErrorsDoNotFanOutIntoPerLineRequests(HttpStatusCode status)
    {
        int calls = 0;
        using var http = new HttpClient(new ScriptableHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("failure") });
        }));
        using var translator = new HttpOpenAiTranslator("http://localhost/v1", "key", "model", http);
        await Assert.ThrowsExactlyAsync<TranslatorException>(() => translator.TranslateAsync(["Hello", "World"], "zh"));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, translator.LastPartialFallbackCount);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RateLimitRetriesAreBoundedAndRespectRetryAfter(bool eventuallySucceeds)
    {
        int calls = 0;
        using var http = new HttpClient(new ScriptableHandler((_, _) =>
        {
            int call = Interlocked.Increment(ref calls);
            if (eventuallySucceeds && call == 3)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"choices":[{"message":{"content":"1. 你好\n2. 世界"}}]}"""),
                });
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("limited") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }));
        using var translator = new HttpOpenAiTranslator("http://localhost/v1", null, "model", http);
        if (eventuallySucceeds)
        {
            CollectionAssert.AreEqual(new[] { "你好", "世界" }, (await translator.TranslateAsync(["Hello", "World"], "zh")).ToArray());
        }
        else
        {
            await Assert.ThrowsExactlyAsync<TranslatorException>(() => translator.TranslateAsync(["Hello", "World"], "zh"));
        }

        Assert.AreEqual(3, calls, "two retries total; no per-line fan-out");
    }

    [TestMethod]
    public async Task FatalChunkFailureCancelsAndDrainsOtherRequests()
    {
        int started = 0;
        int active = 0;
        int cancelled = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new ScriptableHandler(async (_, token) =>
        {
            int call = Interlocked.Increment(ref started);
            Interlocked.Increment(ref active);
            try
            {
                if (call == TranslatorBatch.MaxConcurrentHttpChunks)
                {
                    allStarted.TrySetResult();
                }

                await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                if (call == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("invalid key") };
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }));
        using var translator = new HttpOpenAiTranslator("http://localhost/v1", null, "model", http);
        var texts = Enumerable.Range(0, TranslatorBatch.HttpMaxLines * 4).Select(i => "Line " + i).ToArray();
        await Assert.ThrowsExactlyAsync<TranslatorException>(() => translator.TranslateAsync(texts, "zh").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(TranslatorBatch.MaxConcurrentHttpChunks, started);
        Assert.AreEqual(0, active, "TranslateAsync must await all started requests");
        Assert.AreEqual(TranslatorBatch.MaxConcurrentHttpChunks - 1, cancelled);
    }

    [TestMethod]
    public async Task InjectedHttpClientKeepsItsAddressAndDefaultHeaders()
    {
        using var http = new HttpClient(new ScriptableHandler((request, _) =>
        {
            Assert.AreEqual("http://target.example/v1/chat/completions", request.RequestUri.AbsoluteUri);
            Assert.AreEqual("request-key", request.Headers.Authorization.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"你好"}}]}"""),
            });
        })) { BaseAddress = new Uri("http://original.example/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "original-key");
        using var translator = new HttpOpenAiTranslator("http://target.example/v1", "request-key", "model", http);
        await translator.TranslateAsync(["Hello"], "zh");
        Assert.AreEqual("http://original.example/", http.BaseAddress.AbsoluteUri);
        Assert.AreEqual("original-key", http.DefaultRequestHeaders.Authorization.Parameter);
    }

    private sealed class ScriptableHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _impl;

        public ScriptableHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl)
            => _impl = impl;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _impl(request, cancellationToken);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OversizedSuccessResponseStopsReadingWithoutPerLineFallback(bool knownLength)
    {
        int calls = 0;
        using var source = new CountingStream(HttpOpenAiTranslator.MaxResponseBytes + 100_000, knownLength);
        using var http = new HttpClient(new ScriptableHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) });
        }));
        using var translator = new HttpOpenAiTranslator("http://localhost/v1", null, "model", http);
        var error = await Assert.ThrowsExactlyAsync<TranslatorException>(() => translator.TranslateAsync(["Hello", "World"], "zh"));
        Assert.IsFalse(error.AllowBatchFallback);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(source.BytesRead <= HttpOpenAiTranslator.MaxResponseBytes + 1);
        if (knownLength)
        {
            Assert.AreEqual(0, source.BytesRead);
        }
    }

    [TestMethod]
    public async Task OversizedErrorResponseOnlyReadsBoundedPrefix()
    {
        using var source = new CountingStream(10_000_000, knownLength: false);
        using var http = new HttpClient(new ScriptableHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StreamContent(source) })));
        using var translator = new HttpOpenAiTranslator("http://localhost/v1", null, "model", http);
        var error = await Assert.ThrowsExactlyAsync<TranslatorException>(() => translator.TranslateAsync(["Hello"], "zh"));
        Assert.IsFalse(error.AllowBatchFallback);
        StringAssert.Contains(error.Message, "401");
        Assert.AreEqual(HttpOpenAiTranslator.MaxErrorResponseBytes, source.BytesRead);
    }

    [TestMethod]
    public async Task ResponseBodyRemainsSubjectToHttpClientTimeout()
    {
        using var source = new CountingStream(1, knownLength: false, stall: true);
        using var http = new HttpClient(new ScriptableHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) })))
        {
            Timeout = TimeSpan.FromMilliseconds(200),
        };
        using var translator = new HttpOpenAiTranslator("http://localhost/v1", null, "model", http);
        var error = await Assert.ThrowsExactlyAsync<TranslatorException>(
            () => translator.TranslateAsync(["Hello"], "zh").WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(error.AllowBatchFallback);
    }

    private sealed class CountingStream(int length, bool knownLength, bool stall = false) : Stream
    {
        public int BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => knownLength;

        public override bool CanWrite => false;

        public override long Length => knownLength ? length : throw new NotSupportedException();

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int size = Math.Min(count, length - BytesRead);
            buffer.AsSpan(offset, size).Fill((byte)'x');
            BytesRead += size;
            return size;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stall)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            int size = Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..size].Fill((byte)'x');
            BytesRead += size;
            return size;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
