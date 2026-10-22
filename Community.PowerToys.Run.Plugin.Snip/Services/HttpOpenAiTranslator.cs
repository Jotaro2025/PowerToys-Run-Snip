// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly bool _ownsClient;

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
            _http = new HttpClient
            {
                // Short screenshot texts; keep the ceiling generous for cold starts.
                Timeout = TimeSpan.FromMinutes(2),
            };
            _ownsClient = true;
        }

        var root = baseUrl.TrimEnd('/');
        _http.BaseAddress = new Uri(root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root + "/" : root + "/");

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new TranslatorException(Properties.Resources.tr_err_no_language);
        }

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
            return results;
        }

        var chunks = TranslatorBatch.Chunk(work, TranslatorBatch.HttpMaxLines, TranslatorBatch.HttpMaxChars);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lines = chunk.Select(c => c.Text).ToList();
            try
            {
                var reply = await CompleteAsync(
                    TranslatorBatch.BuildNumberedPrompt(targetLanguage, lines),
                    maxTokens: Math.Clamp(lines.Sum(l => l.Length) * 4, 128, 4096),
                    cancellationToken).ConfigureAwait(false);

                if (TranslatorBatch.TryParseNumberedReply(reply, lines.Count, out var parsed))
                {
                    for (int i = 0; i < chunk.Count; i++)
                    {
                        results[chunk[i].Index] = parsed[i];
                    }

                    continue;
                }

                Log.Info(
                    $"Snip HTTP translator batch parse failed for {lines.Count} lines; falling back to per-line.",
                    typeof(HttpOpenAiTranslator));
            }
            catch (TranslatorException)
            {
                // Fall through to per-line for this chunk.
                Log.Info(
                    $"Snip HTTP translator batch request failed for {lines.Count} lines; falling back to per-line.",
                    typeof(HttpOpenAiTranslator));
            }

            foreach (var (index, text) in chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results[index] = await TranslateOneAsync(text, targetLanguage, cancellationToken).ConfigureAwait(false);
            }
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

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.PostAsync("chat/completions", content, cancellationToken).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslatorException($"{Properties.Resources.tr_err_http} {(int)response.StatusCode}: {Truncate(raw, 200)}");
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
        catch (TranslatorException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip HTTP translator failed: {ex.Message}", ex, typeof(HttpOpenAiTranslator));
            throw new TranslatorException($"{Properties.Resources.tr_err_http} {ex.Message}", ex);
        }
    }

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
