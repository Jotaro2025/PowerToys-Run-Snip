// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Downloads the WeChatOcr NuGet package and extracts <c>content/wco_data/**</c>
/// into a LocalAppData cache so slim plugin zips need not ship ~61 MB of OCR assets.
/// Keep <see cref="PackageVersion"/> in sync with the WeChatOcr PackageReference.
/// </summary>
/// <remarks>
/// Study-only: the OCR binaries come from the same WeChatOcr package (NuGet description:
/// study only, not for commercial use). Commercial products must evaluate compliance.
/// </remarks>
public static class OcrAssetsDownloader
{
    /// <summary>Must match PackageReference Include="WeChatOcr" Version.</summary>
    public const string PackageId = "wechatocr";

    /// <summary>Must match PackageReference Include="WeChatOcr" Version.</summary>
    public const string PackageVersion = "1.0.5";

    /// <summary>Entries under this prefix in the nupkg become the on-disk <c>wco_data</c> tree.</summary>
    public const string NupkgContentPrefix = "content/wco_data/";

    /// <summary>Primary: nuget.org flat container.</summary>
    public static string NugetOrgUrl
        => $"https://api.nuget.org/v3-flatcontainer/{PackageId}/{PackageVersion}/{PackageId}.{PackageVersion}.nupkg";

    /// <summary>Fallback: Azure China NuGet CDN (often reachable in mainland China).</summary>
    public static string AzureChinaCdnUrl
        => $"https://nuget.cdn.azure.cn/v3/flatcontainer/{PackageId}/{PackageVersion}/{PackageId}.{PackageVersion}.nupkg";

    private static readonly HttpClient Http = CreateClient();

    private static readonly (string Name, string Url)[] DownloadSources =
    {
        ("nuget.org", NugetOrgUrl),
        ("Azure China CDN", AzureChinaCdnUrl),
    };

    /// <summary>
    /// <c>%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\wco_data</c>
    /// </summary>
    public static string DefaultCacheDirectory => WeChatOcrService.CachedOcrDataDirectory;

    /// <summary>True when required OCR files are present under <paramref name="dataDirectory"/>.</summary>
    public static bool IsComplete(string dataDirectory)
        => dataDirectory != null && !WeChatOcrService.FindMissingFiles(dataDirectory).Any();

    /// <summary>
    /// Ensures a usable <c>wco_data</c> exists (plugin folder or LocalAppData cache).
    /// Downloads the WeChatOcr nupkg and extracts <c>content/wco_data</c> when needed.
    /// </summary>
    public static Task<(bool Ok, string Path, string Error)> EnsureDownloadedAsync(
        IProgress<OcrAssetsDownloadProgress> progress = null,
        bool force = false,
        CancellationToken cancellationToken = default)
        => EnsureDownloadedAsync(Http, DefaultCacheDirectory, progress, force, cancellationToken);

    /// <summary>Test / DI entry with injectable client and target directory.</summary>
    internal static async Task<(bool Ok, string Path, string Error)> EnsureDownloadedAsync(
        HttpClient httpClient,
        string cacheDirectory,
        IProgress<OcrAssetsDownloadProgress> progress = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        if (!force)
        {
            var existing = WeChatOcrService.ResolveOcrDataDirectory();
            if (existing != null)
            {
                progress?.Report(new OcrAssetsDownloadProgress(0, 0, done: true, phase: "ready", sourceName: null));
                return (true, existing, null);
            }
        }

        string parent = Path.GetDirectoryName(cacheDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(parent))
        {
            return (false, null, "Invalid OCR cache path.");
        }

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return (false, null, $"{Properties.Resources.ocr_err_download_network} {ex.Message}".Trim());
        }

        string staging = cacheDirectory + ".staging";
        string nupkgPath = Path.Combine(parent, $"wechatocr-{PackageVersion}.nupkg.downloading");

        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);

            Exception lastError = null;
            foreach (var (name, url) in DownloadSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report(new OcrAssetsDownloadProgress(0, null, done: false, phase: "download", sourceName: name));
                    await DownloadNupkgAsync(httpClient, url, nupkgPath, name, progress, cancellationToken).ConfigureAwait(false);

                    progress?.Report(new OcrAssetsDownloadProgress(0, null, done: false, phase: "extract", sourceName: name));
                    ExtractWcoData(nupkgPath, staging);

                    if (!IsComplete(staging))
                    {
                        var missing = string.Join(", ", WeChatOcrService.FindMissingFiles(staging));
                        throw new InvalidOperationException($"Extracted OCR assets incomplete: {missing}");
                    }

                    // Replace cache atomically-ish: delete old, move staging into place.
                    if (Directory.Exists(cacheDirectory))
                    {
                        Directory.Delete(cacheDirectory, recursive: true);
                    }

                    Directory.Move(staging, cacheDirectory);
                    TryDeleteFile(nupkgPath);

                    progress?.Report(new OcrAssetsDownloadProgress(0, 0, done: true, phase: "ready", sourceName: name));
                    return (true, cacheDirectory, null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Log.Exception($"Snip OCR assets download from {name} failed: {ex.Message}", ex, typeof(OcrAssetsDownloader));
                    TryDeleteDirectory(staging);
                    Directory.CreateDirectory(staging);
                    TryDeleteFile(nupkgPath);
                }
            }

            string detail = lastError?.Message ?? string.Empty;
            return (false, null, $"{Properties.Resources.ocr_err_download_all_sources} {detail}".Trim());
        }
        catch (OperationCanceledException)
        {
            return (false, null, Properties.Resources.ocr_err_download_cancelled);
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteFile(nupkgPath);
        }
    }

    /// <summary>Extracts <c>content/wco_data/**</c> from a nupkg into <paramref name="destinationWcoData"/>.</summary>
    internal static void ExtractWcoData(string nupkgPath, string destinationWcoData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nupkgPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationWcoData);

        Directory.CreateDirectory(destinationWcoData);

        using var zip = ZipFile.OpenRead(nupkgPath);
        bool hasContentPrefix = zip.Entries.Any(e =>
            (e.FullName ?? string.Empty).Replace('\\', '/').StartsWith(NupkgContentPrefix, StringComparison.OrdinalIgnoreCase));

        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            // Prefer content/wco_data/ (one copy). Only use contentFiles/.../wco_data/ if content/ is absent.
            string normalized = entry.FullName.Replace('\\', '/');
            if (hasContentPrefix)
            {
                if (!normalized.StartsWith(NupkgContentPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            string relative = TryGetWcoRelativePath(entry.FullName);
            if (relative == null)
            {
                continue;
            }

            // Zip-slip guard.
            string dest = Path.GetFullPath(Path.Combine(destinationWcoData, relative));
            string root = Path.GetFullPath(destinationWcoData)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(dest, Path.GetFullPath(destinationWcoData), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Refusing to extract outside OCR cache: {entry.FullName}");
            }

            string destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    /// <summary>Maps a nupkg entry path to a path relative to <c>wco_data</c>, or null if not an OCR asset.</summary>
    internal static string TryGetWcoRelativePath(string entryFullName)
    {
        if (string.IsNullOrEmpty(entryFullName))
        {
            return null;
        }

        string normalized = entryFullName.Replace('\\', '/');
        if (normalized.StartsWith(NupkgContentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ToOsRelativePath(normalized.AsSpan(NupkgContentPrefix.Length));
        }

        // Fallback: contentFiles/any/<tfm>/wco_data/...
        const string marker = "/wco_data/";
        int idx = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0 && normalized.StartsWith("contentFiles/", StringComparison.OrdinalIgnoreCase))
        {
            return ToOsRelativePath(normalized.AsSpan(idx + marker.Length));
        }

        return null;
    }

    /// <summary>One allocation: slash-normalize a UTF-16 relative path slice.</summary>
    private static string ToOsRelativePath(ReadOnlySpan<char> relative)
    {
        if (relative.IsEmpty)
        {
            return string.Empty;
        }

        if (Path.DirectorySeparatorChar == '/')
        {
            return relative.ToString();
        }

        return string.Create(relative.Length, relative, static (dest, src) =>
        {
            src.CopyTo(dest);
            for (int i = 0; i < dest.Length; i++)
            {
                if (dest[i] == '/')
                {
                    dest[i] = Path.DirectorySeparatorChar;
                }
            }
        });
    }

    private static async Task DownloadNupkgAsync(
        HttpClient httpClient,
        string url,
        string targetPath,
        string sourceName,
        IProgress<OcrAssetsDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            targetPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 64,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[64 * 1024];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;
            progress?.Report(new OcrAssetsDownloadProgress(downloaded, total, done: false, phase: "download", sourceName: sourceName));
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PowerToys-Run-Snip-OCR/" + PackageVersion);
        return client;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Progress snapshot for <see cref="OcrAssetsDownloader.EnsureDownloadedAsync"/>.</summary>
public readonly struct OcrAssetsDownloadProgress
{
    public OcrAssetsDownloadProgress(long downloadedBytes, long? totalBytes, bool done, string phase, string sourceName = null)
    {
        DownloadedBytes = downloadedBytes;
        TotalBytes = totalBytes;
        Done = done;
        Phase = phase;
        SourceName = sourceName;
    }

    public long DownloadedBytes { get; }

    public long? TotalBytes { get; }

    public bool Done { get; }

    /// <summary><c>download</c>, <c>extract</c>, or <c>ready</c>.</summary>
    public string Phase { get; }

    public string SourceName { get; }

    public double Percent
        => TotalBytes is > 0 ? Math.Clamp(100.0 * DownloadedBytes / TotalBytes.Value, 0, 100) : 0;
}
