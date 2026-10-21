// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Win32;
using WeChatOcr;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Text extraction (OCR) through the OCR component of the locally installed
/// WeChat for Windows, driven by the <c>WeChatOcr</c> NuGet package.
/// </summary>
/// <remarks>
/// WeChat 3.9.x keeps <c>mmmojo.dll</c> / <c>mmmojo_64.dll</c> in a versioned
/// sub-folder of its install folder (e.g. <c>C:\Program Files\Tencent\WeChat\[3.9.12.51]</c>)
/// and downloads <c>WeChatOCR.exe</c> on demand to
/// <c>%APPDATA%\Tencent\WeChat\XPlugin\Plugins\WeChatOCR</c>. WeChatOcr copies the
/// mmmojo DLLs into <c>wco_data</c> under the plugin folder and starts
/// WeChatOCR.exe; nothing of WeChat is shipped with the plugin.
/// </remarks>
public static class WeChatOcrService
{
    private const string MmmojoDll = "mmmojo.dll";
    private const string Mmmojo64Dll = "mmmojo_64.dll";
    private const string OcrExeName = "WeChatOCR.exe";
    private const int MaxAttempts = 3;

    /// <summary>
    /// WeChatOcr runs one task per call and drops it silently when the OCR
    /// process is not connected yet, so each attempt gets a deadline and is retried.
    /// </summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(4);

    /// <summary>WeChatOcr keeps process-wide state; run one recognition at a time.</summary>
    private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Recognizes the text in a PNG image. Runs off the calling thread.
    /// </summary>
    /// <param name="png">PNG-encoded image.</param>
    /// <param name="customWeChatDir">WeChat install folder from settings; empty = detect automatically.</param>
    /// <param name="cancellationToken">Cancels waiting for the result.</param>
    /// <returns>Recognized lines joined by new lines; empty when no text was found.</returns>
    /// <exception cref="OcrException">WeChat or its OCR component is missing, or OCR failed.</exception>
    public static async Task<string> RecognizeAsync(byte[] png, string customWeChatDir, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new OcrException(Properties.Resources.ocr_err_arch);
        }

        // Everything below touches the disk, the registry or a child process: keep it off the UI thread.
        return await Task.Run(
            async () =>
            {
                var weChatDir = LocateWeChatDir(customWeChatDir);
                if (!HasOcrComponent())
                {
                    throw new OcrException(Properties.Resources.ocr_err_component_not_found);
                }

                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await RunOcrAsync(png, weChatDir, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Gate.Release();
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the WeChat folder that holds the mmmojo DLLs, or throws a
    /// user-facing <see cref="OcrException"/> explaining why none was found.
    /// </summary>
    private static string LocateWeChatDir(string customWeChatDir)
    {
        if (!string.IsNullOrWhiteSpace(customWeChatDir))
        {
            return ResolveWeChatDir(customWeChatDir)
                ?? throw new OcrException($"{Properties.Resources.ocr_err_custom_dir_invalid} {customWeChatDir.Trim()}");
        }

        foreach (var candidate in GetDefaultWeChatDirs())
        {
            var dir = ResolveWeChatDir(candidate);
            if (dir != null)
            {
                return dir;
            }
        }

        throw new OcrException(IsWeChat4Installed()
            ? Properties.Resources.ocr_err_wechat4
            : Properties.Resources.ocr_err_wechat_not_found);
    }

    /// <summary>
    /// Resolves a WeChat install folder to the folder that actually holds the
    /// mmmojo DLLs: the folder itself, or its newest <c>[x.y.z.w]</c> sub-folder.
    /// </summary>
    /// <returns>The resolved folder, or <c>null</c> when it is not a WeChat 3.x folder.</returns>
    public static string ResolveWeChatDir(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        root = root.Trim().Trim('"').TrimEnd('\\', '/');
        try
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            if (HasMmmojo(root))
            {
                return root;
            }

            return Directory.EnumerateDirectories(root)
                .Where(d =>
                {
                    var name = Path.GetFileName(d);
                    return name.StartsWith('[') && name.EndsWith(']') && HasMmmojo(d);
                })
                .OrderByDescending(d => ParseVersion(Path.GetFileName(d)))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Joins the recognized lines of a WeChatOcr result.</summary>
    public static string JoinLines(WeChatOcrResult result)
    {
        var lines = result?.OcrResult?.SingleResult;
        if (lines == null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (!string.IsNullOrEmpty(line?.SingleStrUtf8))
            {
                sb.AppendLine(line.SingleStrUtf8);
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>User-facing "text extraction failed" message with the technical detail appended.</summary>
    public static string FailedMessage(string detail)
        => $"{Properties.Resources.ocr_err_failed} {detail}";

    private static async Task<string> RunOcrAsync(byte[] png, string weChatDir, CancellationToken cancellationToken)
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"snip-ocr-{Guid.NewGuid():N}.png");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await File.WriteAllBytesAsync(imagePath, png, cancellationToken).ConfigureAwait(false);

            // WeChatOcr resolves its "wco_data" folder (where it copies WeChat's
            // mmmojo DLLs and loads them from) against this base directory. The
            // default is the process working directory, which is PowerToys'
            // install folder; keep it in the (user-writable) plugin folder.
            DataLocation.SetBaseDirectory(PluginDirectory);
            DropStaleMmmojoCopies(weChatDir);

            using (var ocr = new ImageOcr(weChatDir))
            {
                for (int attempt = 0; attempt < MaxAttempts && !tcs.Task.IsCompleted; attempt++)
                {
                    ocr.Run(imagePath, (path, result) =>
                    {
                        // WeChatOcr may report progress with a null result; wait for the real one.
                        if (result != null)
                        {
                            tcs.TrySetResult(JoinLines(result));
                        }
                    });

                    await Task.WhenAny(tcs.Task, Task.Delay(AttemptTimeout, cancellationToken)).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            if (!tcs.Task.IsCompleted)
            {
                throw new OcrException(Properties.Resources.ocr_err_timeout);
            }

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OcrException && ex is not OperationCanceledException)
        {
            Log.Exception($"Snip WeChat OCR failed: {ex.Message}", ex, typeof(WeChatOcrService));
            throw new OcrException(FailedMessage(ex.Message), ex);
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    /// <summary>
    /// WeChatOcr only copies the mmmojo DLLs when they are missing; drop copies
    /// left from an older WeChat so they are refreshed after a WeChat update.
    /// Copies already loaded in this process are locked and simply kept.
    /// </summary>
    private static void DropStaleMmmojoCopies(string weChatDir)
    {
        foreach (var name in new[] { MmmojoDll, Mmmojo64Dll })
        {
            var copy = Path.Combine(DataLocation.WeChatOcrData, name);
            var source = Path.Combine(weChatDir, name);
            if (File.Exists(copy)
                && File.Exists(source)
                && FileVersionInfo.GetVersionInfo(copy).FileVersion != FileVersionInfo.GetVersionInfo(source).FileVersion)
            {
                TryDelete(copy);
            }
        }
    }

    private static string PluginDirectory
        => Path.GetDirectoryName(typeof(WeChatOcrService).Assembly.Location) ?? AppContext.BaseDirectory;

    private static bool HasMmmojo(string dir)
        => File.Exists(Path.Combine(dir, MmmojoDll)) && File.Exists(Path.Combine(dir, Mmmojo64Dll));

    private static Version ParseVersion(string folderName)
        => Version.TryParse(folderName.Trim('[', ']'), out var v) ? v : new Version(0, 0);

    /// <summary>
    /// WeChatOCR.exe is a WeChat plugin downloaded on first use of WeChat's own
    /// "extract text" feature; WeChatOcr looks for it here.
    /// </summary>
    private static bool HasOcrComponent()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Tencent",
            "WeChat",
            "XPlugin",
            "Plugins",
            "WeChatOCR");
        try
        {
            return Directory.Exists(root)
                && Directory.EnumerateFiles(root, OcrExeName, SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> GetDefaultWeChatDirs()
    {
        var registryDir = ReadRegistryString(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WeChat", "InstallLocation");
        if (!string.IsNullOrEmpty(registryDir))
        {
            yield return registryDir;
        }

        registryDir = ReadRegistryString(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Tencent\WeChat", "InstallPath");
        if (!string.IsNullOrEmpty(registryDir))
        {
            yield return registryDir;
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "WeChat");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "WeChat");
    }

    private static bool IsWeChat4Installed()
    {
        return Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "Weixin"))
            || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "Weixin"));
    }

    private static string ReadRegistryString(RegistryHive hive, RegistryView view, string subKey, string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Temp file; Windows cleans the temp folder eventually.
        }
    }
}
