// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// OpenAI-compatible <c>/chat/completions</c> translator (HyMT2Sharp.Server or any compatible API).
/// Batches numbered lines into one (or few) requests; falls back to per-line if parsing fails.
/// </summary>
public sealed class HttpOpenAiTranslator : ITranslator, IDisposable
{
    internal const int MaxResponseBytes = 4 * 1024 * 1024;
    internal const int MaxErrorResponseBytes = 16 * 1024;
    private static readonly SocketsHttpHandler SharedHandler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly bool _ownsClient;

    /// <summary>Number of lines that fell back to original source text in the last <see cref="TranslateAsync"/> call.</summary>
    public int LastPartialFallbackCount { get; private set; }

    public HttpOpenAiTranslator(string baseUrl, string apiKey, string model, HttpClient httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        _model = model;
        if (httpClient != null)
        {
            _http = httpClient;
            _ownsClient = false;
        }
        else
        {
            _http = new HttpClient(SharedHandler, disposeHandler: false)
            {
                // Short screenshot texts; keep the ceiling generous for cold starts.
                Timeout = TimeSpan.FromMinutes(2),
            };
            _ownsClient = true;
        }

        var root = baseUrl.Trim().TrimEnd('/') + "/";
        _endpoint = new Uri(new Uri(root), "chat/completions");
        _apiKey = apiKey?.Trim();
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        IProgress<TranslatorProgress> progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new TranslatorException(Properties.Resources.tr_err_no_language);
        }

        LastPartialFallbackCount = 0;
        var results = new string[texts.Count];
        for (int i = 0; i < texts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i]))
            {
                results[i] = string.Empty;
            }
        }

        var work = TranslatorBatch.CollectNonEmpty(texts);
        if (work.Count == 0)
        {
            return results;
        }

        // Single line: keep the classic one-shot prompt (no numbering overhead).
        if (work.Count == 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[work[0].Index] = await TranslateOneAsync(work[0].Text, targetLanguage, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new TranslatorProgress(1, 1));
            return results;
        }

        LastPartialFallbackCount = 0;
        int completed = 0;
        int failedLines = 0;
        TranslatorException firstFailure = null;
        var chunks = TranslatorBatch.Chunk(work, TranslatorBatch.HttpMaxLines, TranslatorBatch.HttpMaxChars);

        // Remote round-trips dominate; run a few chunks at a time. Result slots
        // are index-disjoint, so order is preserved without locks.
        async Task ProcessChunkAsync(List<(int Index, string Text)> chunk, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var lines = chunk.Select(c => c.Text).ToList();
            try
            {
                var reply = await CompleteAsync(
                    TranslatorBatch.BuildNumberedPrompt(targetLanguage, lines),
                    maxTokens: Math.Clamp(lines.Sum(l => l.Length) * 4, 128, 4096),
                    token).ConfigureAwait(false);

                if (TranslatorBatch.TryParseNumberedReply(reply, lines.Count, out var parsed))
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        results[chunk[i].Index] = parsed[i];
                    }

                    int done = Interlocked.Add(ref completed, chunk.Count);
                    progress?.Report(new TranslatorProgress(done, work.Count));
                    return;
                }

                Log.Info(
                    $"Snip HTTP translator batch parse failed for {lines.Count} lines; falling back to per-line.",
                    typeof(HttpOpenAiTranslator));
            }
            catch (TranslatorException ex) when (ex.AllowBatchFallback)
            {
                // Fall through to per-line for this chunk.
                Log.Info(
                    $"Snip HTTP translator batch request failed for {lines.Count} lines; falling back to per-line.",
                    typeof(HttpOpenAiTranslator));
            }

            foreach (var (index, text) in chunk)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    results[index] = await TranslateOneAsync(text, targetLanguage, token).ConfigureAwait(false);
                }
                catch (TranslatorException ex) when (ex.AllowBatchFallback)
                {
                    Interlocked.CompareExchange(ref firstFailure, ex, null);
                    Interlocked.Increment(ref failedLines);
                    results[index] = text;
                }

                int done = Interlocked.Increment(ref completed);
                progress?.Report(new TranslatorProgress(done, work.Count));
            }
        }

        await Parallel.ForEachAsync(
            chunks,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = TranslatorBatch.MaxConcurrentHttpChunks,
                CancellationToken = cancellationToken,
            },
            async (chunk, token) =>
            {
                token.ThrowIfCancellationRequested();
                await ProcessChunkAsync(chunk, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        if (failedLines >= work.Count && firstFailure != null)
        {
            throw firstFailure;
        }

        if (failedLines > 0)
        {
            LastPartialFallbackCount = failedLines;
            TranslatorBatch.RecordPartialFallback(failedLines, work.Count);
        }

        return results;
    }

    private async Task<string> TranslateOneAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        return await CompleteAsync(
            TranslatorPrompts.Build(targetLanguage, text),
            maxTokens: Math.Clamp(text.Length * 4, 64, 1024),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CompleteAsync(string prompt, int maxTokens, CancellationToken cancellationToken)
    {
        var body = new
        {
            model = _model,
            temperature = 0,
            max_tokens = maxTokens,
            messages = new[]
            {
                new { role = "user", content = prompt },
            },
        };

        string payload = JsonSerializer.Serialize(body);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrEmpty(_apiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                }

                // ResponseHeadersRead avoids HttpClient's unbounded pre-buffering.
                // Its own timeout ends at headers, so retain the deadline while reading.
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptTimeout.CancelAfter(_http.Timeout);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptTimeout.Token).ConfigureAwait(false);
                var raw = await ReadResponseAsync(response, attemptTimeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    bool transient = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                        or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                    var retryAfter = response.Headers.RetryAfter;
                    TimeSpan delay = retryAfter?.Delta
                        ?? (retryAfter?.Date is DateTimeOffset date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(250 * (1 << attempt)));
                    if (transient && attempt < 2 && delay <= TimeSpan.FromSeconds(30))
                    {
                        await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new TranslatorException($"{Properties.Resources.tr_err_http} {(int)response.StatusCode}: {Truncate(raw, 200)}")
                    {
                        AllowBatchFallback = false,
                    };
                }

                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                {
                    throw new TranslatorException(Properties.Resources.tr_err_http_empty);
                }

                var message = choices[0].GetProperty("message");
                var translated = message.TryGetProperty("content", out var c) ? c.GetString() : null;
                return (translated ?? string.Empty).Trim();
            }
        }
        catch (TranslatorException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip HTTP translator failed: {ex.Message}", ex, typeof(HttpOpenAiTranslator));
            throw new TranslatorException($"{Properties.Resources.tr_err_http} {ex.Message}", ex)
            {
                AllowBatchFallback = ex is JsonException or InvalidOperationException or KeyNotFoundException,
            };
        }
    }

    private static async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        bool success = response.IsSuccessStatusCode;
        int limit = success ? MaxResponseBytes : MaxErrorResponseBytes;
        if (success && response.Content.Headers.ContentLength > limit)
        {
            throw ResponseTooLarge();
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (output.Length <= limit)
            {
                int remaining = limit - (int)output.Length;
                if (!success && remaining == 0)
                {
                    break;
                }

                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + (success ? 1 : 0))), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > limit)
                {
                    throw ResponseTooLarge();
                }

                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        output.Position = 0;
        string charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        var encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        using var reader = new StreamReader(output, encoding, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TranslatorException ResponseTooLarge() => new(Properties.Resources.tr_err_http_too_large)
    {
        AllowBatchFallback = false,
    };

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : string.Concat(s.AsSpan(0, max), "…");
}
