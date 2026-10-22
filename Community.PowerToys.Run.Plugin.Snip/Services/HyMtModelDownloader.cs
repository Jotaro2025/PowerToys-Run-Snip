// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Downloads the recommended Hy-MT2 2Bit GGUF into a LocalAppData cache.
/// Weights are never shipped with the plugin. Sources are tried in order
/// (optional custom HTTPS → ModelScope → hf-mirror → Hugging Face) so mainland China can fetch without HF.
/// </summary>
public static class HyMtModelDownloader
{
    /// <summary>Primary: ModelScope (魔搭) — preferred in mainland China.</summary>
    public const string ModelScopeUrl =
        "https://www.modelscope.cn/api/v1/models/Tencent-Hunyuan/Hy-MT2-1.8B-2Bit-GGUF/repo?Revision=master&FilePath=Hy-MT2-1.8B-2Bit.gguf";

    /// <summary>Fallback: Hugging Face China mirror.</summary>
    public const string HfMirrorUrl =
        "https://hf-mirror.com/tencent/Hy-MT2-1.8B-2Bit-GGUF/resolve/main/Hy-MT2-1.8B-2Bit.gguf";

    /// <summary>Last resort: official Hugging Face.</summary>
    public const string HuggingFaceUrl =
        "https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF/resolve/main/Hy-MT2-1.8B-2Bit.gguf";

    /// <summary>Kept for callers/docs; equals <see cref="ModelScopeUrl"/> (China-first default).</summary>
    public const string DownloadUrl = ModelScopeUrl;

    public const string FileName = "Hy-MT2-1.8B-2Bit.gguf";

    /// <summary>Exact size published by ModelScope / Hugging Face / Sdcb.GgufWeights (600,534,880 bytes).</summary>
    public const long ExpectedSizeBytes = 600_534_880L;

    /// <summary>Size floor used for idempotent skip (incomplete downloads are below this).</summary>
    public const long MinValidSizeBytes = 500_000_000L;

    /// <summary>SHA-256 of the official GGUF (optional integrity check after download).</summary>
    public const string ExpectedSha256 = "dcc33bbae9b28d923c8c76a64f6157840841d26f8774f3dfd770d5fabeeb1cd7";

    private static readonly HttpClient Http = CreateClient();

    private static readonly (string Name, string Url)[] DownloadSources =
    {
        ("ModelScope", ModelScopeUrl),
        ("hf-mirror", HfMirrorUrl),
        ("Hugging Face", HuggingFaceUrl),
    };

    /// <summary>
    /// <c>%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\models\Hy-MT2-1.8B-2Bit.gguf</c>
    /// </summary>
    public static string DefaultCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerToys",
        "RunPlugins",
        "Community.PowerToys.Run.Plugin.Snip",
        "models",
        FileName);

    /// <summary>True when <paramref name="path"/> exists and is large enough to be a complete GGUF.</summary>
    public static bool IsValidCachedFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            return new FileInfo(path).Length >= MinValidSizeBytes;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns an existing usable GGUF path: explicit settings path first, then the default cache.
    /// </summary>
    public static string ResolveGgufPath(SnipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(settings.HyMtGgufPath))
        {
            var explicitPath = settings.HyMtGgufPath.Trim();
            if (IsValidCachedFile(explicitPath))
            {
                return explicitPath;
            }
        }

        var sidecar = HyMtGgufPathPersister.TryReadSidecarPath();
        if (sidecar != null)
        {
            return sidecar;
        }

        var cache = DefaultCachePath;
        return IsValidCachedFile(cache) ? cache : null;
    }

    /// <summary>
    /// Builds the download source list. A non-empty HTTPS <paramref name="customUrl"/>
    /// is tried first; otherwise (or when invalid) the China-friendly defaults remain.
    /// </summary>
    public static (string Name, string Url)[] BuildDownloadSources(string customUrl = null)
    {
        if (TryNormalizeHttpsUrl(customUrl, out var url, out _))
        {
            return new[]
            {
                ("Custom", url),
                ("ModelScope", ModelScopeUrl),
                ("hf-mirror", HfMirrorUrl),
                ("Hugging Face", HuggingFaceUrl),
            };
        }

        return DownloadSources;
    }

    /// <summary>Accepts only absolute <c>https://</c> URLs (rejects http / relative).</summary>
    public static bool TryNormalizeHttpsUrl(string raw, out string url, out string error)
    {
        url = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "empty";
            return false;
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "https required";
            return false;
        }

        url = uri.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// Ensures the recommended GGUF is present in the cache. Skips the network when a valid
    /// file already exists unless <paramref name="force"/> is set. Tries ModelScope, then
    /// hf-mirror, then Hugging Face; a mid-download failure moves to the next source.
    /// </summary>
    /// <returns><c>(ok, path, error)</c>; <c>error</c> is localized Chinese/English on failure.</returns>
    public static async Task<(bool Ok, string Path, string Error)> EnsureDownloadedAsync(
        IProgress<HyMtDownloadProgress> progress = null,
        bool force = false,
        string customDownloadUrl = null,
        CancellationToken cancellationToken = default)
    {
        var target = DefaultCachePath;
        if (!force && IsValidCachedFile(target))
        {
            progress?.Report(new HyMtDownloadProgress(ExpectedSizeBytes, ExpectedSizeBytes, done: true, sourceName: null));
            return (true, target, null);
        }

        string directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                return (false, null, Properties.Resources.tr_err_download_disk_full);
            }
            catch (IOException ex)
            {
                return (false, null, FormatNetworkError(ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                return (false, null, FormatNetworkError(ex.Message));
            }
        }

        string tempPath = target + ".downloading";
        var failures = new List<string>();

        try
        {
            foreach (var (name, url) in BuildDownloadSources(customDownloadUrl))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryDelete(tempPath);

                progress?.Report(new HyMtDownloadProgress(0, ExpectedSizeBytes, done: false, sourceName: name));

                try
                {
                    var (ok, error, fatal) = await TryDownloadFromSourceAsync(
                        url,
                        name,
                        tempPath,
                        progress,
                        cancellationToken).ConfigureAwait(false);

                    if (ok)
                    {
                        if (File.Exists(target))
                        {
                            File.Delete(target);
                        }

                        File.Move(tempPath, target);
                        var length = new FileInfo(target).Length;
                        progress?.Report(new HyMtDownloadProgress(length, ExpectedSizeBytes, done: true, sourceName: name));
                        Log.Info($"Snip Hy-MT2 GGUF downloaded from {name} → {target}", typeof(HyMtModelDownloader));
                        return (true, target, null);
                    }

                    TryDelete(tempPath);

                    if (fatal)
                    {
                        // Disk full / cancelled: do not burn through remaining mirrors.
                        return (false, null, error);
                    }

                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        failures.Add($"{name}: {error}");
                        Log.Warn($"Snip Hy-MT2 download from {name} failed: {error}", typeof(HyMtModelDownloader));
                    }
                }
                catch (OperationCanceledException)
                {
                    TryDelete(tempPath);
                    return (false, null, Properties.Resources.tr_err_download_cancelled);
                }
            }

            TryDelete(tempPath);
            string detail = failures.Count > 0
                ? string.Join("; ", failures)
                : string.Empty;
            return (false, null, FormatAllSourcesFailed(detail));
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            return (false, null, Properties.Resources.tr_err_download_cancelled);
        }
    }

    /// <summary>
    /// Attempts one URL. Returns <c>fatal=true</c> for cancel / disk-full (stop trying sources).
    /// </summary>
    private static async Task<(bool Ok, string Error, bool Fatal)> TryDownloadFromSourceAsync(
        string url,
        string sourceName,
        string tempPath,
        IProgress<HyMtDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"{(int)response.StatusCode} {response.ReasonPhrase}", Fatal: false);
            }

            long? total = response.Content.Headers.ContentLength;
            if (total is null or <= 0)
            {
                total = ExpectedSizeBytes;
            }

            await using (Stream remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var local = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 256,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 256];
                long downloaded = 0;
                int read;
                while ((read = await remote.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloaded += read;
                    progress?.Report(new HyMtDownloadProgress(downloaded, total, done: false, sourceName: sourceName));
                }

                await local.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var info = new FileInfo(tempPath);
            if (info.Length < MinValidSizeBytes)
            {
                return (false, Properties.Resources.tr_err_download_incomplete, Fatal: false);
            }

            try
            {
                string hash = await ComputeSha256HexAsync(tempPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn(
                        $"Snip Hy-MT2 GGUF SHA-256 mismatch from {sourceName} (got {hash}, expected {ExpectedSha256}); keeping file because size looks complete.",
                        typeof(HyMtModelDownloader));
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"Snip Hy-MT2 GGUF hash skipped ({sourceName}): {ex.Message}", ex, typeof(HyMtModelDownloader));
            }

            return (true, null, Fatal: false);
        }
        catch (OperationCanceledException)
        {
            return (false, Properties.Resources.tr_err_download_cancelled, Fatal: true);
        }
        catch (HttpRequestException ex)
        {
            return (false, ex.Message, Fatal: false);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            return (false, Properties.Resources.tr_err_download_disk_full, Fatal: true);
        }
        catch (IOException ex)
        {
            // Broken connection mid-stream → try next mirror.
            return (false, ex.Message, Fatal: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, ex.Message, Fatal: true);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, Fatal: false);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromHours(2),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PowerToys-Run-Snip/1.0 (+https://github.com/Jotaro2025/PowerToys-Run-Snip)");
        return client;
    }

    private static string FormatNetworkError(string detail)
        => $"{Properties.Resources.tr_err_download_network} {detail}".Trim();

    private static string FormatAllSourcesFailed(string detail)
    {
        string baseMsg = Properties.Resources.tr_err_download_all_sources;
        return string.IsNullOrWhiteSpace(detail) ? baseMsg : $"{baseMsg} {detail}".Trim();
    }

    private static bool IsDiskFull(IOException ex)
    {
        // ERROR_DISK_FULL (112), ERROR_HANDLE_DISK_FULL (39)
        const int errorDiskFull = unchecked((int)0x80070070);
        const int errorHandleDiskFull = unchecked((int)0x80070027);
        return ex.HResult == errorDiskFull || ex.HResult == errorHandleDiskFull
            || (ex.Message?.Contains("disk", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<string> ComputeSha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 256,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>Progress snapshot for <see cref="HyMtModelDownloader.EnsureDownloadedAsync"/>.</summary>
public readonly struct HyMtDownloadProgress
{
    public HyMtDownloadProgress(long downloadedBytes, long? totalBytes, bool done, string sourceName = null)
    {
        DownloadedBytes = downloadedBytes;
        TotalBytes = totalBytes;
        Done = done;
        SourceName = sourceName;
    }

    public long DownloadedBytes { get; }

    public long? TotalBytes { get; }

    public bool Done { get; }

    /// <summary>Current mirror label (ModelScope / hf-mirror / Hugging Face), if known.</summary>
    public string SourceName { get; }

    public double Percent
        => TotalBytes is > 0 ? Math.Clamp(100.0 * DownloadedBytes / TotalBytes.Value, 0, 100) : 0;
}
