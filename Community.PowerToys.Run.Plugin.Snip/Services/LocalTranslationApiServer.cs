// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Minimal loopback HTTP/1.1 server for the local translation engine. Binds
/// 127.0.0.1 only, requires a bearer token, validates Host (DNS rebinding),
/// enforces an origin allowlist, request size limits and rate limits, and closes
/// the connection after one request.
/// </summary>
internal sealed class LocalTranslationApiServer : IDisposable
{
    internal const int MaxHeaderBytes = 16 * 1024;
    internal const int MaxBodyBytes = 1024 * 1024;
    internal const int MaxTexts = 200;
    internal const int MaxTextChars = 4000;
    internal const int MaxTotalChars = 20000;
    internal const int DefaultMaxRequestsPerMinute = 120;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _startLock = new();
    private readonly object _rateLock = new();
    private readonly Queue<long> _requestTimes = new();
    private readonly string _token;
    private readonly string[] _allowedOrigins;
    private readonly Func<IReadOnlyList<string>, string, CancellationToken, Task<IReadOnlyList<string>>> _translate;
    private readonly int _maxRequestsPerMinute;
    private readonly SemaphoreSlim _concurrency = new(4, 4);

    private TcpListener _listener;
    private CancellationTokenSource _cts;
    private volatile bool _running;

    internal LocalTranslationApiServer(
        int port,
        string token,
        IReadOnlyList<string> allowedOrigins,
        Func<IReadOnlyList<string>, string, CancellationToken, Task<IReadOnlyList<string>>> translate,
        int maxRequestsPerMinute = DefaultMaxRequestsPerMinute)
    {
        RequestedPort = port;
        _token = token ?? string.Empty;
        _allowedOrigins = allowedOrigins?.Where(o => !string.IsNullOrWhiteSpace(o)).ToArray() ?? [];
        _translate = translate ?? throw new ArgumentNullException(nameof(translate));
        _maxRequestsPerMinute = Math.Max(1, maxRequestsPerMinute);
    }

    /// <summary>Port requested at construction (0 lets the OS choose for tests).</summary>
    internal int RequestedPort { get; }

    /// <summary>Actual bound port, or 0 before start / after failure.</summary>
    public int Port { get; private set; }

    /// <summary>True while the accept loop is running.</summary>
    public bool IsRunning => _running;

    /// <summary>Last start failure message (null when healthy).</summary>
    public string LastError { get; private set; }

    /// <summary>Starts listening; failure is reported through <see cref="LastError"/>.</summary>
    public void Start()
    {
        lock (_startLock)
        {
            if (_running)
            {
                return;
            }

            try
            {
                var listener = new TcpListener(IPAddress.Loopback, RequestedPort);
                listener.Start();
                _listener = listener;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _cts = new CancellationTokenSource();
                _running = true;
                LastError = null;

                _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
                Log.Info($"Snip local translate API listening on 127.0.0.1:{Port}", typeof(LocalTranslationApiServer));
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _running = false;
                Port = 0;
                Log.Warn($"Snip local translate API failed to start: {ex.Message}", typeof(LocalTranslationApiServer));
            }
        }
    }

    /// <summary>Stops accepting connections and closes the listener.</summary>
    public void Stop()
    {
        lock (_startLock)
        {
            if (!_running && _listener == null)
            {
                return;
            }

            _running = false;
            try
            {
                _cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
            }

            _listener = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            _ = HandleClientSafeAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientSafeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        bool acquired = false;
        try
        {
            acquired = await _concurrency.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                await WriteJsonAsync(
                    client.GetStream(),
                    429,
                    "Too Many Requests",
                    "{\"error\":\"too many concurrent requests\"}",
                    null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Server stopping / client gone.
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip local translate API request failed: {ex.Message}", typeof(LocalTranslationApiServer));
        }
        finally
        {
            if (acquired)
            {
                _concurrency.Release();
            }

            try
            {
                client.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;

        var stream = client.GetStream();
        var (method, target, headers, bodyPrefix) = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
        if (method == null)
        {
            await WriteJsonAsync(stream, 400, "Bad Request", "{\"error\":\"malformed request\"}", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        string origin = headers.TryGetValue("origin", out var originValue) ? originValue.Trim() : null;
        string allowOrigin = ResolveAllowOrigin(origin);
        if (origin != null && _allowedOrigins.Length > 0 && allowOrigin == null)
        {
            await WriteJsonAsync(stream, 403, "Forbidden", "{\"error\":\"origin not allowed\"}", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsAllowedHost(headers))
        {
            await WriteJsonAsync(stream, 400, "Bad Request", "{\"error\":\"invalid host\"}", allowOrigin, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(method, "OPTIONS", StringComparison.Ordinal))
        {
            await WritePreflightAsync(stream, allowOrigin, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsAuthorized(headers))
        {
            await WriteJsonAsync(stream, 401, "Unauthorized", "{\"error\":\"missing or invalid bearer token\"}", allowOrigin, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(method, "GET", StringComparison.Ordinal)
            && target.Equals("/v1/health", StringComparison.OrdinalIgnoreCase))
        {
            string json = JsonSerializer.Serialize(
                new { status = "ok", engine = "hymt", port = Port },
                JsonOptions);
            await WriteJsonAsync(stream, 200, "OK", json, allowOrigin, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(method, "POST", StringComparison.Ordinal)
            || !target.Equals("/v1/translate", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stream, 404, "Not Found", "{\"error\":\"not found\"}", allowOrigin, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryAcquireRateLimit())
        {
            await WriteJsonAsync(stream, 429, "Too Many Requests", "{\"error\":\"rate limit exceeded\"}", allowOrigin, cancellationToken).ConfigureAwait(false);
            return;
        }

        var bodyReader = new RequestBodyReader(bodyPrefix, stream);
        byte[] body;
        bool chunked = headers.TryGetValue("transfer-encoding", out var transferEncoding)
            && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);
        if (chunked)
        {
            var (chunkedBody, chunkedError) = await ReadChunkedBodyAsync(bodyReader, cancellationToken).ConfigureAwait(false);
            if (chunkedError != 0)
            {
                string reason = chunkedError == 413 ? "Payload Too Large" : "Bad Request";
                string errorJson = chunkedError == 413
                    ? "{\"error\":\"request body too large\"}"
                    : "{\"error\":\"invalid chunked body\"}";
                await WriteJsonAsync(
                    stream,
                    chunkedError,
                    reason,
                    errorJson,
                    allowOrigin,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            body = chunkedBody;
        }
        else
        {
            if (!headers.TryGetValue("content-length", out var contentLengthText)
                || !int.TryParse(contentLengthText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int contentLength)
                || contentLength < 0)
            {
                await WriteJsonAsync(stream, 411, "Length Required", "{\"error\":\"content-length required\"}", allowOrigin, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (contentLength > MaxBodyBytes)
            {
                await WriteJsonAsync(stream, 413, "Payload Too Large", "{\"error\":\"request body too large\"}", allowOrigin, cancellationToken).ConfigureAwait(false);
                return;
            }

            body = await bodyReader.ReadExactAsync(contentLength, cancellationToken).ConfigureAwait(false);
            if (body == null)
            {
                return;
            }
        }

        TranslateRequest request;
        try
        {
            request = JsonSerializer.Deserialize<TranslateRequest>(body, JsonOptions);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(stream, 400, "Bad Request", "{\"error\":\"invalid json\"}", allowOrigin, cancellationToken).ConfigureAwait(false);
            return;
        }

        var (validationStatus, validationError) = ValidateTranslateRequest(request);
        if (validationError != null)
        {
            await WriteJsonAsync(
                stream,
                validationStatus,
                validationStatus == 400 ? "Bad Request" : "Payload Too Large",
                $"{{\"error\":\"{validationError}\"}}",
                allowOrigin,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var translations = await _translate(
                request.Texts,
                request.Target ?? string.Empty,
                cancellationToken).ConfigureAwait(false);

            if (translations == null || translations.Count != request.Texts.Count)
            {
                await WriteJsonAsync(stream, 502, "Bad Gateway", "{\"error\":\"translator returned an unexpected result\"}", allowOrigin, cancellationToken).ConfigureAwait(false);
                return;
            }

            string json = JsonSerializer.Serialize(
                new { translations, count = translations.Count },
                JsonOptions);
            await WriteJsonAsync(stream, 200, "OK", json, allowOrigin, cancellationToken).ConfigureAwait(false);
        }
        catch (LocalApiUnavailableException ex)
        {
            await WriteJsonAsync(stream, 503, "Service Unavailable", ErrorJson(ex.Message), allowOrigin, cancellationToken).ConfigureAwait(false);
        }
        catch (TranslatorException ex)
        {
            await WriteJsonAsync(stream, 503, "Service Unavailable", ErrorJson(ex.Message), allowOrigin, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or server stopping; no response needed.
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip local translate API translation failed: {ex.Message}", typeof(LocalTranslationApiServer));
            await WriteJsonAsync(stream, 500, "Internal Server Error", "{\"error\":\"translation failed\"}", allowOrigin, cancellationToken).ConfigureAwait(false);
        }
    }

    private static (int Status, string Error) ValidateTranslateRequest(TranslateRequest request)
    {
        if (request?.Texts == null || request.Texts.Count == 0)
        {
            return (400, "texts is required");
        }

        if (request.Texts.Count > MaxTexts)
        {
            return (413, "too many texts");
        }

        long total = 0;
        foreach (string text in request.Texts)
        {
            if (text == null)
            {
                return (400, "texts must not contain null");
            }

            if (text.Length > MaxTextChars)
            {
                return (413, "text too long");
            }

            total += text.Length;
            if (total > MaxTotalChars)
            {
                return (413, "texts too large");
            }
        }

        return (0, null);
    }

    private static string ErrorJson(string message)
        => JsonSerializer.Serialize(new { error = message ?? "unavailable" }, JsonOptions);

    private bool TryAcquireRateLimit()
    {
        long now = Environment.TickCount64;
        lock (_rateLock)
        {
            while (_requestTimes.Count > 0 && now - _requestTimes.Peek() > 60_000)
            {
                _requestTimes.Dequeue();
            }

            if (_requestTimes.Count >= _maxRequestsPerMinute)
            {
                return false;
            }

            _requestTimes.Enqueue(now);
            return true;
        }
    }

    private bool IsAuthorized(IReadOnlyDictionary<string, string> headers)
    {
        if (_token.Length == 0 || !headers.TryGetValue("authorization", out var value))
        {
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes("Bearer " + _token);
        byte[] actual = Encoding.UTF8.GetBytes(value.Trim());
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private bool IsAllowedHost(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("host", out var host) || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string value = host.Trim();
        int colon = value.LastIndexOf(':');
        string name = colon > 0 ? value[..colon] : value;
        if (colon > 0
            && (!int.TryParse(value[(colon + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int port)
                || port != Port))
        {
            return false;
        }

        return name.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || name.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveAllowOrigin(string origin)
    {
        if (_allowedOrigins.Length == 0)
        {
            return "*";
        }

        if (string.IsNullOrEmpty(origin))
        {
            return null;
        }

        foreach (string allowed in _allowedOrigins)
        {
            if (string.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
            {
                return origin;
            }
        }

        return null;
    }

    private async Task WritePreflightAsync(NetworkStream stream, string allowOrigin, CancellationToken cancellationToken)
    {
        if (allowOrigin == null)
        {
            await WriteJsonAsync(stream, 403, "Forbidden", "{\"error\":\"origin not allowed\"}", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var head = new StringBuilder()
            .Append("HTTP/1.1 204 No Content\r\n")
            .Append("Access-Control-Allow-Origin: ").Append(allowOrigin).Append("\r\n")
            .Append("Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n")
            .Append("Access-Control-Allow-Headers: authorization, content-type\r\n")
            .Append("Access-Control-Max-Age: 600\r\n")
            .Append("Vary: Origin\r\n")
            .Append("Content-Length: 0\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        NetworkStream stream,
        int status,
        string reason,
        string json,
        string allowOrigin,
        CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n");

        if (allowOrigin != null)
        {
            head.Append("Access-Control-Allow-Origin: ").Append(allowOrigin).Append("\r\n");
            head.Append("Vary: Origin\r\n");
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(byte[] Body, int ErrorStatus)> ReadChunkedBodyAsync(
        RequestBodyReader reader,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        while (true)
        {
            string line = await reader.ReadLineAsync(8192, cancellationToken).ConfigureAwait(false);
            if (line == null)
            {
                return (null, 400);
            }

            int semicolon = line.IndexOf(';');
            string hex = semicolon >= 0 ? line[..semicolon] : line;
            if (!int.TryParse(hex.Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int size)
                || size < 0)
            {
                return (null, 400);
            }

            if (size == 0)
            {
                // Consume optional trailers up to the terminating empty line.
                while (true)
                {
                    string trailer = await reader.ReadLineAsync(8192, cancellationToken).ConfigureAwait(false);
                    if (trailer == null)
                    {
                        return (null, 400);
                    }

                    if (trailer.Length == 0)
                    {
                        break;
                    }
                }

                break;
            }

            if (output.Length + size > MaxBodyBytes)
            {
                return (null, 413);
            }

            byte[] chunk = await reader.ReadExactAsync(size, cancellationToken).ConfigureAwait(false);
            byte[] crlf = await reader.ReadExactAsync(2, cancellationToken).ConfigureAwait(false);
            if (chunk == null || crlf == null || crlf[0] != '\r' || crlf[1] != '\n')
            {
                return (null, 400);
            }

            output.Write(chunk);
        }

        return (output.ToArray(), 0);
    }

    private static async Task<(string Method, string Target, Dictionary<string, string> Headers, byte[] BodyPrefix)> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxHeaderBytes);
        try
        {
            int used = 0;
            int headerEnd = -1;
            while (used < MaxHeaderBytes)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(used, MaxHeaderBytes - used), cancellationToken).ConfigureAwait(false);
                if (n <= 0)
                {
                    return (null, null, null, []);
                }

                used += n;
                headerEnd = FindHeaderEnd(buffer, used);
                if (headerEnd >= 0)
                {
                    break;
                }
            }

            if (headerEnd < 0)
            {
                return (null, null, null, []);
            }

            string text = Encoding.Latin1.GetString(buffer, 0, headerEnd);
            string[] lines = text.Split("\r\n", StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return (null, null, null, []);
            }

            string[] requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length < 2)
            {
                return (null, null, null, []);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    break;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    return (null, null, null, []);
                }

                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            int prefixLength = Math.Max(0, used - (headerEnd + 4));
            byte[] bodyPrefix = prefixLength > 0
                ? buffer.AsSpan(headerEnd + 4, prefixLength).ToArray()
                : [];
            return (requestLine[0], requestLine[1], headers, bodyPrefix);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> buffer, int used)
    {
        for (int i = 0; i <= used - 4; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class RequestBodyReader
    {
        private readonly byte[] _prefix;
        private readonly NetworkStream _stream;
        private int _position;

        public RequestBodyReader(byte[] prefix, NetworkStream stream)
        {
            _prefix = prefix ?? [];
            _stream = stream;
        }

        public async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
        {
            if (count == 0)
            {
                return [];
            }

            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
                if (n <= 0)
                {
                    return null;
                }

                read += n;
            }

            return buffer;
        }

        public async Task<string> ReadLineAsync(int maxLength, CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            while (true)
            {
                int value = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                if (value < 0)
                {
                    return null;
                }

                if (value == '\r')
                {
                    int next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
                    return next == '\n' ? builder.ToString() : null;
                }

                if (value == '\n')
                {
                    return builder.ToString();
                }

                if (builder.Length >= maxLength)
                {
                    return null;
                }

                builder.Append((char)value);
            }
        }

        private async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (_position < _prefix.Length)
            {
                int n = Math.Min(destination.Length, _prefix.Length - _position);
                _prefix.AsSpan(_position, n).CopyTo(destination.Span);
                _position += n;
                return n;
            }

            return await _stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_position < _prefix.Length)
            {
                return _prefix[_position++];
            }

            var one = new byte[1];
            int n = await _stream.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false);
            return n == 1 ? one[0] : -1;
        }
    }

    private sealed class TranslateRequest
    {
        public List<string> Texts { get; set; }

        public string Target { get; set; }
    }
}
