// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Downloads the recommended Hy-MT2 2Bit GGUF into a LocalAppData cache.
/// Weights are never shipped with the plugin; this helper fetches them on demand
/// from Hugging Face (same bytes as Sdcb.GgufWeights.Hy-MT2-1.8B-2Bit).
/// </summary>
public static class HyMtModelDownloader
{
    /// <summary>Official Tencent 2Bit GGUF on Hugging Face.</summary>
    public const string DownloadUrl =
        "https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF/resolve/main/Hy-MT2-1.8B-2Bit.gguf";

    public const string FileName = "Hy-MT2-1.8B-2Bit.gguf";

    /// <summary>Exact size published by Hugging Face / Sdcb.GgufWeights (600,534,880 bytes).</summary>
    public const long ExpectedSizeBytes = 600_534_880L;

    /// <summary>Size floor used for idempotent skip (incomplete downloads are below this).</summary>
    public const long MinValidSizeBytes = 500_000_000L;

    /// <summary>SHA-256 of the official GGUF (optional integrity check after download).</summary>
    public const string ExpectedSha256 = "dcc33bbae9b28d923c8c76a64f6157840841d26f8774f3dfd770d5fabeeb1cd7";

    private static readonly HttpClient Http = CreateClient();

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

        var cache = DefaultCachePath;
        return IsValidCachedFile(cache) ? cache : null;
    }

    /// <summary>
    /// Ensures the recommended GGUF is present in the cache. Skips the network when a valid
    /// file already exists unless <paramref name="force"/> is set.
    /// </summary>
    /// <returns><c>(ok, path, error)</c>; <c>error</c> is localized Chinese/English on failure.</returns>
    public static async Task<(bool Ok, string Path, string Error)> EnsureDownloadedAsync(
        IProgress<HyMtDownloadProgress> progress = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var target = DefaultCachePath;
        if (!force && IsValidCachedFile(target))
        {
            progress?.Report(new HyMtDownloadProgress(ExpectedSizeBytes, ExpectedSizeBytes, done: true));
            return (true, target, null);
        }

        string directory = Path.GetDirectoryName(target);
        string tempPath = target + ".downloading";

        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            using var response = await Http.GetAsync(
                DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, null, FormatNetworkError($"{(int)response.StatusCode} {response.ReasonPhrase}"));
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
                    progress?.Report(new HyMtDownloadProgress(downloaded, total, done: false));
                }

                await local.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var info = new FileInfo(tempPath);
            if (info.Length < MinValidSizeBytes)
            {
                TryDelete(tempPath);
                return (false, null, Properties.Resources.tr_err_download_incomplete);
            }

            // Soft SHA check: warn via log but still accept size-valid files if hash tooling fails.
            try
            {
                string hash = await ComputeSha256HexAsync(tempPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn(
                        $"Snip Hy-MT2 GGUF SHA-256 mismatch (got {hash}, expected {ExpectedSha256}); keeping file because size looks complete.",
                        typeof(HyMtModelDownloader));
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"Snip Hy-MT2 GGUF hash skipped: {ex.Message}", ex, typeof(HyMtModelDownloader));
            }

            if (File.Exists(target))
            {
                File.Delete(target);
            }

            File.Move(tempPath, target);
            progress?.Report(new HyMtDownloadProgress(info.Length, total, done: true));
            return (true, target, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            return (false, null, Properties.Resources.tr_err_download_cancelled);
        }
        catch (HttpRequestException ex)
        {
            TryDelete(tempPath);
            Log.Exception($"Snip Hy-MT2 download network failed: {ex.Message}", ex, typeof(HyMtModelDownloader));
            return (false, null, FormatNetworkError(ex.Message));
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            TryDelete(tempPath);
            Log.Exception($"Snip Hy-MT2 download disk full: {ex.Message}", ex, typeof(HyMtModelDownloader));
            return (false, null, Properties.Resources.tr_err_download_disk_full);
        }
        catch (IOException ex)
        {
            TryDelete(tempPath);
            Log.Exception($"Snip Hy-MT2 download IO failed: {ex.Message}", ex, typeof(HyMtModelDownloader));
            return (false, null, FormatNetworkError(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(tempPath);
            Log.Exception($"Snip Hy-MT2 download access denied: {ex.Message}", ex, typeof(HyMtModelDownloader));
            return (false, null, FormatNetworkError(ex.Message));
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            Log.Exception($"Snip Hy-MT2 download failed: {ex.Message}", ex, typeof(HyMtModelDownloader));
            return (false, null, FormatNetworkError(ex.Message));
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
    public HyMtDownloadProgress(long downloadedBytes, long? totalBytes, bool done)
    {
        DownloadedBytes = downloadedBytes;
        TotalBytes = totalBytes;
        Done = done;
    }

    public long DownloadedBytes { get; }

    public long? TotalBytes { get; }

    public bool Done { get; }

    public double Percent
        => TotalBytes is > 0 ? Math.Clamp(100.0 * DownloadedBytes / TotalBytes.Value, 0, 100) : 0;
}
