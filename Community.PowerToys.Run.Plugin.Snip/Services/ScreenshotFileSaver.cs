// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Writes PNG screenshots to disk, resolving the target directory and making
/// sure an existing file is never overwritten.
/// </summary>
public static class ScreenshotFileSaver
{
    public const string FileNamePrefix = "snapshot";

    /// <summary>File name prefix for translated images saved from the result window.</summary>
    public const string TranslatedFileNamePrefix = "translate";

    /// <summary>
    /// Resolves the save directory: trims the configured value, falls back when
    /// it is empty, contains invalid path characters, or is not rooted (e.g. a
    /// typo like "shots" or "C:\Sho<ts"), so a bad setting degrades to the
    /// default folder instead of failing every auto-save.
    /// </summary>
    public static string ResolveDirectory(string configured, string fallbackDirectory)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return fallbackDirectory;
        }

        var candidate = configured.Trim().TrimEnd('\\', '/');

        // GetInvalidPathChars only carries control characters; < > | " etc. come
        // from GetInvalidFileNameChars. Check the directory's name segments
        // (excluding the drive prefix) against the stricter set.
        bool invalid = candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0;
        if (!invalid)
        {
            foreach (var segment in candidate.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment.Length == 0 || segment.EndsWith(':'))
                {
                    continue; // drive root or empty middle segment
                }

                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    invalid = true;
                    break;
                }
            }
        }

        if (invalid || !Path.IsPathRooted(candidate))
        {
            return fallbackDirectory;
        }

        return candidate;
    }

    public static string BuildFileName()
        => string.Format(CultureInfo.InvariantCulture, "{0}_{1:yyyyMMdd_HHmmss_fff}.png", FileNamePrefix, DateTime.Now);

    /// <summary>Suggested PNG file name for a translated image (Save File dialog default).</summary>
    public static string BuildTranslatedFileName()
        => string.Format(CultureInfo.InvariantCulture, "{0}_{1:yyyyMMdd_HHmmss_fff}.png", TranslatedFileNamePrefix, DateTime.Now);

    public static string BuildUniquePath(string directory)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, string.Format(CultureInfo.InvariantCulture, "{0}_{1}.png", FileNamePrefix, stamp));
        if (!File.Exists(path))
        {
            return path;
        }

        for (var i = 1; i < 1000; i++)
        {
            path = Path.Combine(directory, string.Format(CultureInfo.InvariantCulture, "{0}_{1}_{2}.png", FileNamePrefix, stamp, i));
            if (!File.Exists(path))
            {
                return path;
            }
        }

        return Path.Combine(directory, string.Format(CultureInfo.InvariantCulture, "{0}_{1}_{2:N}.png", FileNamePrefix, stamp, Guid.NewGuid()));
    }

    public static async Task<(bool Ok, string Path, string Error)> SavePngAsync(
        byte[] pngBytes, string configuredDirectory, string fallbackDirectory)
    {
        var directory = ResolveDirectory(configuredDirectory, fallbackDirectory);
        try
        {
            if (!Directory.Exists(directory))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (Exception ex)
                {
                    return (false, null, $"Failed to create directory: {directory}\r\n{ex.Message}");
                }
            }

            var path = BuildUniquePath(directory);
            await using var fs = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await fs.WriteAsync(pngBytes).ConfigureAwait(false);
            await fs.FlushAsync().ConfigureAwait(false);
            return (true, path, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (false, null, $"Access denied: {directory}\r\n{ex.Message}");
        }
        catch (IOException ex) when (ex is not DirectoryNotFoundException)
        {
            return (false, null, $"Failed to write file under {directory}\r\n{ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }
}
