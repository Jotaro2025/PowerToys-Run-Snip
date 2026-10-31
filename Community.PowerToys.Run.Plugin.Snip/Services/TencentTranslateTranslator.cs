// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Tencent Cloud Machine Translation (TMT) translator using the <c>TextTranslate</c>
/// action, TC3-HMAC-SHA256 signed. Requires a SecretId / SecretKey pair from the
/// Tencent Cloud console (the free tier covers 5M characters per month).
/// Batches several OCR lines into one request (newline separated) and falls back to
/// per-line calls when the reply does not preserve the line count.
/// </summary>
public sealed class TencentTranslateTranslator : ITranslator, IDisposable
{
    internal const string Endpoint = "https://tmt.tencentcloudapi.com";
    internal const string Host = "tmt.tencentcloudapi.com";
    internal const string Service = "tmt";
    internal const string ActionName = "TextTranslate";
    internal const string ApiVersion = "2018-03-21";
    internal const string DefaultRegion = "ap-guangzhou";

    /// <summary><c>TextTranslate</c> accepts at most 2000 characters per request.</summary>
    internal const int MaxSourceChars = 2000;

    private static readonly HashSet<string> SupportedTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "zh", "zh-TW", "en", "ja", "ko", "fr", "es", "it", "de", "tr",
        "ru", "pt", "vi", "id", "th", "ms", "ar", "hi",
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _secretId;
    private readonly string _secretKey;
    private readonly string _region;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Number of lines that fell back to the original source text in the last call.</summary>
    public int LastPartialFallbackCount { get; private set; }

    public TencentTranslateTranslator(
        string secretId,
        string secretKey,
        string region,
        HttpClient httpClient = null,
        Func<DateTimeOffset> clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);

        _secretId = secretId.Trim();
        _secretKey = secretKey.Trim();
        _region = string.IsNullOrWhiteSpace(region) ? DefaultRegion : region.Trim();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        if (httpClient != null)
        {
            _http = httpClient;
            _ownsClient = false;
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _ownsClient = true;
        }
    }

    /// <summary>
    /// Maps the free-text "target language" setting to a TMT language code. Accepts a
    /// code directly (<c>zh</c>, <c>en</c>, …) or common Chinese / English names
    /// (<c>简体中文</c>, <c>英语</c>, <c>Japanese</c>, …).
    /// </summary>
    internal static string ResolveLanguageCode(string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new TranslatorException(Properties.Resources.tr_err_no_language);
        }

        string text = targetLanguage.Trim();
        if (SupportedTargets.Contains(text))
        {
            return CanonicalCode(text);
        }

        if (ContainsAny(text, "繁体", "繁體", "正體", "正体", "traditional", "zh-tw", "zh-hant"))
        {
            return "zh-TW";
        }

        if (ContainsAny(text, "中文", "简体", "簡體", "汉语", "漢語", "chinese", "zh", "cmn"))
        {
            return "zh";
        }

        if (ContainsAny(text, "英", "english", "en"))
        {
            return "en";
        }

        if (ContainsAny(text, "日", "japanese", "ja"))
        {
            return "ja";
        }

        if (ContainsAny(text, "韩", "韓", "朝鲜", "한국", "한글", "korean", "ko"))
        {
            return "ko";
        }

        if (ContainsAny(text, "法", "french", "fr"))
        {
            return "fr";
        }

        if (ContainsAny(text, "德", "german", "de"))
        {
            return "de";
        }

        if (ContainsAny(text, "西", "spanish", "es"))
        {
            return "es";
        }

        if (ContainsAny(text, "葡", "portuguese", "pt"))
        {
            return "pt";
        }

        if (ContainsAny(text, "意", "italian", "it"))
        {
            return "it";
        }

        if (ContainsAny(text, "俄", "russian", "ru"))
        {
            return "ru";
        }

        if (ContainsAny(text, "土耳其", "turkish", "tr"))
        {
            return "tr";
        }

        if (ContainsAny(text, "越南", "vietnamese", "vi"))
        {
            return "vi";
        }

        if (ContainsAny(text, "泰", "thai", "th"))
        {
            return "th";
        }

        if (ContainsAny(text, "印尼", "印度尼西亚", "indonesian", "id"))
        {
            return "id";
        }

        if (ContainsAny(text, "马来", "馬來", "malay", "ms"))
        {
            return "ms";
        }

        if (ContainsAny(text, "阿拉伯", "arabic", "ar"))
        {
            return "ar";
        }

        if (ContainsAny(text, "印地", "hindi", "hi"))
        {
            return "hi";
        }

        throw new TranslatorException($"{Properties.Resources.tr_err_tencent_lang} ({targetLanguage.Trim()})");
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        IProgress<TranslatorProgress> progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        string target = ResolveLanguageCode(targetLanguage);

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

        if (work.Count == 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[work[0].Index] = await TranslateTextAsync(work[0].Text, target, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(new TranslatorProgress(1, 1));
            return results;
        }

        LastPartialFallbackCount = 0;
        int completed = 0;
        int failedLines = 0;
        TranslatorException firstFailure = null;
        var chunks = TranslatorBatch.Chunk(work, TranslatorBatch.HttpMaxLines, TranslatorBatch.HttpMaxChars);

        // Remote round-trips dominate; run a few chunks at a time (3 keeps the
        // default 5 QPS limit comfortable). Result slots are index-disjoint.
        var throttle = new SemaphoreSlim(
            TranslatorBatch.MaxConcurrentHttpChunks,
            TranslatorBatch.MaxConcurrentHttpChunks);
        var tasks = new List<Task>(chunks.Count);

        async Task ProcessChunkAsync(List<(int Index, string Text)> chunk)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lines = chunk.Select(c => c.Text).ToList();
                try
                {
                    var reply = await TranslateTextAsync(
                        string.Join("\n", lines),
                        target,
                        cancellationToken).ConfigureAwait(false);

                    var split = SplitLines(reply);
                    if (split.Count == lines.Count)
                    {
                        for (int i = 0; i < chunk.Count; i++)
                        {
                            results[chunk[i].Index] = split[i];
                        }

                        int done = Interlocked.Add(ref completed, chunk.Count);
                        progress?.Report(new TranslatorProgress(done, work.Count));
                        return;
                    }

                    Log.Info(
                        $"Snip Tencent translator line count mismatch ({split.Count} vs {lines.Count}); falling back to per-line.",
                        typeof(TencentTranslateTranslator));
                }
                catch (TranslatorException)
                {
                    Log.Info(
                        $"Snip Tencent translator batch request failed for {lines.Count} lines; falling back to per-line.",
                        typeof(TencentTranslateTranslator));
                }

                foreach (var (index, text) in chunk)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        results[index] = await TranslateTextAsync(text, target, cancellationToken).ConfigureAwait(false);
                    }
                    catch (TranslatorException ex)
                    {
                        Interlocked.CompareExchange(ref firstFailure, ex, null);
                        Interlocked.Increment(ref failedLines);
                        results[index] = text;
                    }

                    int done = Interlocked.Increment(ref completed);
                    progress?.Report(new TranslatorProgress(done, work.Count));
                }
            }
            finally
            {
                throttle.Release();
            }
        }

        foreach (var chunk in chunks)
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            tasks.Add(ProcessChunkAsync(chunk));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

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

    /// <summary>Request body; the exact bytes matter because the signature hashes them.</summary>
    internal static string BuildPayload(string sourceText, string targetCode)
    {
        var body = new
        {
            SourceText = Truncate(sourceText, MaxSourceChars),
            Source = "auto",
            Target = targetCode,
            ProjectId = 0,
        };

        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// TC3-HMAC-SHA256 <c>Authorization</c> header for one request
    /// (https://cloud.tencent.com/document/api/551/15619).
    /// </summary>
    internal static string BuildAuthorization(string secretId, string secretKey, string payload, long timestamp)
    {
        string date = DateTimeOffset.FromUnixTimeSeconds(timestamp)
            .UtcDateTime
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        string canonicalRequest = string.Join(
            "\n",
            "POST",
            "/",
            string.Empty,
            $"content-type:application/json; charset=utf-8\nhost:{Host}\n",
            "content-type;host",
            Sha256Hex(payload));

        string scope = $"{date}/{Service}/tc3_request";
        string stringToSign = string.Join(
            "\n",
            "TC3-HMAC-SHA256",
            timestamp.ToString(CultureInfo.InvariantCulture),
            scope,
            Sha256Hex(canonicalRequest));

        byte[] secretDate = HmacSha256(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        byte[] secretService = HmacSha256(secretDate, Service);
        byte[] secretSigning = HmacSha256(secretService, "tc3_request");
        string signature = Convert.ToHexString(HmacSha256(secretSigning, stringToSign)).ToLowerInvariant();

        return $"TC3-HMAC-SHA256 Credential={secretId}/{scope}, SignedHeaders=content-type;host, Signature={signature}";
    }

    private async Task<string> TranslateTextAsync(string text, string targetCode, CancellationToken cancellationToken)
    {
        string payload = BuildPayload(text, targetCode);
        long timestamp = _clock().ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            BuildAuthorization(_secretId, _secretKey, payload, timestamp));
        request.Headers.TryAddWithoutValidation("X-TC-Action", ActionName);
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-TC-Version", ApiVersion);
        request.Headers.TryAddWithoutValidation("X-TC-Region", _region);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslatorException(
                    $"{Properties.Resources.tr_err_tencent} HTTP {(int)response.StatusCode}: {Truncate(raw, 200)}");
            }

            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("Response", out var body))
            {
                throw new TranslatorException(Properties.Resources.tr_err_tencent);
            }

            if (body.TryGetProperty("Error", out var error))
            {
                string code = error.TryGetProperty("Code", out var c) ? c.GetString() : null;
                string message = error.TryGetProperty("Message", out var m) ? m.GetString() : null;
                throw new TranslatorException($"{Properties.Resources.tr_err_tencent} {code}: {message}".TrimEnd());
            }

            if (!body.TryGetProperty("TargetText", out var target) || target.ValueKind != JsonValueKind.String)
            {
                throw new TranslatorException(Properties.Resources.tr_err_tencent);
            }

            return (target.GetString() ?? string.Empty).Trim();
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
            Log.Exception($"Snip Tencent translator failed: {ex.Message}", ex, typeof(TencentTranslateTranslator));
            throw new TranslatorException($"{Properties.Resources.tr_err_tencent} {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        foreach (var span in (text ?? string.Empty).AsSpan().EnumerateLines())
        {
            lines.Add(span.TrimEnd('\r').ToString());
        }

        // EnumerateLines drops a trailing empty line; callers compare exact counts.
        return lines;
    }

    private static string CanonicalCode(string code)
        => string.Equals(code, "zh-tw", StringComparison.OrdinalIgnoreCase) ? "zh-TW" : code.ToLowerInvariant();

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Sha256Hex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static byte[] HmacSha256(byte[] key, string message)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message));

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s[..max];
}
