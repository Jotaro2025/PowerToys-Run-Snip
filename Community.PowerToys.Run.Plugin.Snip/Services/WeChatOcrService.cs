// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Models;
using WeChatOcr;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Text extraction (OCR) through the <c>WeChatOcr</c> NuGet package, using the
/// OCR component it bundles (WeChatOCR.exe, mmmojo DLLs and model files).
/// </summary>
/// <remarks>
/// Full release zips ship <c>wco_data\</c> next to the plugin; slim zips omit it
/// (~61 MB) and the first OCR / image-translate use downloads the same assets
/// from the WeChatOcr NuGet package into LocalAppData. <see cref="DataLocation"/>
/// is pointed at the directory that contains <c>wco_data</c>. The image handed
/// to the OCR process is written to the user's temp folder.
/// </remarks>
public static class WeChatOcrService
{
    private const int MaxAttempts = 3;

    /// <summary>Files of the bundled OCR component, relative to <c>wco_data</c>.</summary>
    private static readonly string[] RequiredFiles =
    {
        "WeChatOCR.exe",
        "mmmojo_64.dll",
        Path.Combine("Model", "OCRRecogFP32V1.1.0.26.xnet"),
    };

    /// <summary>
    /// WeChatOcr drops a task silently when the OCR process is not connected
    /// yet (e.g. while it starts), so each attempt gets a deadline and is retried.
    /// </summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(4);

    /// <summary>WeChatOcr keeps process-wide state; run one recognition at a time.</summary>
    private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Reused OCR component: launching WeChatOCR.exe and loading the recognition
    /// model per call costs seconds. Owned by <see cref="Gate"/>; a task that
    /// silently drops is retried on the same healthy instance.
    /// </summary>
    private static ImageOcr _sharedOcr;

    /// <summary>
    /// Idle auto-recycle for the shared component's WeChatOCR.exe child process,
    /// mirroring <see cref="HyMT2SharpTranslatorCache.ConfigureIdleUnload(int)"/>
    /// semantics (same setting drives both; 0 disables).
    /// </summary>
    private static TimeSpan _idleTimeout = TimeSpan.FromMinutes(HyMT2SharpTranslatorCache.DefaultIdleUnloadMinutes);
    private static DateTime _lastUseUtc = DateTime.UtcNow;
    private static Timer _idleTimer;

    /// <summary>True when another recognition currently holds the process-wide gate.</summary>
    public static bool IsBusy => Gate.CurrentCount == 0;

    /// <summary>
    /// Starts the WeChatOCR.exe component ahead of the first recognition (the
    /// constructor launches the child process and loads its model, which costs
    /// seconds). Blocking; call on a background thread. Never throws.
    /// </summary>
    public static void Prewarm()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        if (!Gate.Wait(0))
        {
            // A recognition is already running (and has thus warmed the component).
            return;
        }

        try
        {
            _lastUseUtc = DateTime.UtcNow;
            if (_sharedOcr != null)
            {
                return;
            }

            var dataDir = ResolveOcrDataDirectory();
            if (dataDir == null)
            {
                return;
            }

            var baseDir = Path.GetDirectoryName(dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                ?? PluginDirectory;
            DataLocation.SetBaseDirectory(baseDir);
            _sharedOcr = new ImageOcr();
            Log.Info("Snip WeChat OCR prewarm: component started.", typeof(WeChatOcrService));
        }
        catch (Exception ex)
        {
            TryDisposeSharedOcr();
            Log.Warn($"Snip WeChat OCR prewarm failed: {ex.Message}", typeof(WeChatOcrService));
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Current idle timeout for the OCR child process; <see cref="TimeSpan.Zero"/> = keep it warm forever.</summary>
    public static TimeSpan IdleTimeout => _idleTimeout;

    /// <summary>
    /// Sets idle auto-recycle in minutes for the WeChatOCR.exe child process
    /// (0 = off, clamped to 0–<see cref="HyMT2SharpTranslatorCache.MaxIdleUnloadMinutes"/>).
    /// Mirrors the HyMT model idle-unload setting.
    /// </summary>
    public static void ConfigureIdleRecycle(int minutes)
    {
        int m = Math.Clamp(minutes, 0, HyMT2SharpTranslatorCache.MaxIdleUnloadMinutes);
        _idleTimeout = m == 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(m);
        EnsureIdleTimer();
    }

    private static void EnsureIdleTimer()
    {
        if (_idleTimeout == TimeSpan.Zero)
        {
            return;
        }

        _idleTimer ??= new Timer(
            _ =>
            {
                try
                {
                    if (_idleTimeout != TimeSpan.Zero
                        && _sharedOcr != null
                        && DateTime.UtcNow - _lastUseUtc >= _idleTimeout
                        && Gate.Wait(0))
                    {
                        try
                        {
                            TryDisposeSharedOcr();
                            Log.Info("Snip WeChat OCR idle recycle: child process disposed.", typeof(WeChatOcrService));
                        }
                        finally
                        {
                            Gate.Release();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Snip WeChat OCR idle recycle: {ex.Message}", typeof(WeChatOcrService));
                }
            },
            null,
            dueTime: TimeSpan.FromMinutes(1),
            period: TimeSpan.FromMinutes(1));
    }

    /// <summary>OCR assets next to the plugin DLL (full package / local build).</summary>
    public static string PluginOcrDataDirectory => Path.Combine(PluginDirectory, "wco_data");

    /// <summary>
    /// LocalAppData cache used when the slim package omitted <c>wco_data</c>.
    /// </summary>
    public static string CachedOcrDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PowerToys",
        "RunPlugins",
        "Community.PowerToys.Run.Plugin.Snip",
        "wco_data");

    /// <summary>
    /// Resolved <c>wco_data</c> folder (plugin first, then cache), or the plugin
    /// path when neither is complete (for error messages).
    /// </summary>
    public static string OcrDataDirectory => ResolveOcrDataDirectory() ?? PluginOcrDataDirectory;

    /// <summary>True when plugin-dir or LocalAppData cache has the required OCR files.</summary>
    public static bool IsOcrComponentAvailable() => ResolveOcrDataDirectory() != null;

    /// <summary>
    /// Returns a complete <c>wco_data</c> directory (plugin folder preferred), or
    /// <c>null</c> when OCR assets still need to be installed / downloaded.
    /// </summary>
    public static string ResolveOcrDataDirectory()
    {
        if (IsComplete(PluginOcrDataDirectory))
        {
            return PluginOcrDataDirectory;
        }

        if (IsComplete(CachedOcrDataDirectory))
        {
            return CachedOcrDataDirectory;
        }

        return null;
    }

    private static bool IsComplete(string dataDirectory)
        => !FindMissingFiles(dataDirectory).Any();

    /// <summary>
    /// Recognizes the text in a PNG image. Runs off the calling thread.
    /// </summary>
    /// <param name="png">PNG-encoded image.</param>
    /// <param name="cancellationToken">Cancels waiting for the result.</param>
    /// <returns>Recognized lines joined by new lines; empty when no text was found.</returns>
    /// <exception cref="OcrException">The OCR component is missing or unusable, or OCR failed.</exception>
    public static async Task<string> RecognizeAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        var lines = await RecognizeLinesAsync(png, cancellationToken).ConfigureAwait(false);
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.AppendLine(line.Text);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Recognizes text lines with their axis-aligned boxes (for erase+repaint).
    /// Lines are returned in reading order (top-to-bottom, then left-to-right).
    /// </summary>
    public static async Task<IReadOnlyList<OcrTextLine>> RecognizeLinesAsync(
        byte[] png,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);

        // mmmojo_64.dll / WeChatOCR.exe are x64 binaries.
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new OcrException(Properties.Resources.ocr_err_arch);
        }

        // Everything below touches the disk or a child process: keep it off the UI thread.
        return await Task.Run(
            async () =>
            {
                var dataDir = ResolveOcrDataDirectory();
                if (dataDir == null)
                {
                    var missing = FindMissingFiles(PluginOcrDataDirectory).ToList();
                    var hint = missing.Count > 0
                        ? Path.Combine(PluginOcrDataDirectory, missing[0])
                        : PluginOcrDataDirectory;
                    throw new OcrException($"{Properties.Resources.ocr_err_need_install} {hint}");
                }

                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _lastUseUtc = DateTime.UtcNow;
                    return await RunOcrLinesAsync(png, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Gate.Release();
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Recognizes text lines from an already-decoded bitmap. Uses an
    /// uncompressed BMP for the handoff instead of a PNG: the encoder is ~10×
    /// cheaper than WPF's PNG encoder and the native engine reads it directly
    /// (measured ~30 ms less per full-screen call plus the skipped PNG encode).
    /// </summary>
    public static async Task<IReadOnlyList<OcrTextLine>> RecognizeLinesAsync(
        BitmapSource image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new OcrException(Properties.Resources.ocr_err_arch);
        }

        BitmapSource frozen = image;
        if (!frozen.IsFrozen)
        {
            var clone = frozen.Clone();
            clone.Freeze();
            frozen = clone;
        }

        var imagePath = Path.Combine(Path.GetTempPath(), $"snip-ocr-{Guid.NewGuid():N}.bmp");
        try
        {
            await Task.Run(
                () =>
                {
                    using var stream = new FileStream(
                        imagePath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 64 * 1024,
                        FileOptions.SequentialScan);
                    var encoder = new BmpBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(frozen));
                    encoder.Save(stream);
                },
                cancellationToken).ConfigureAwait(false);

            return await RunOcrPathAsync(imagePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    /// <summary>Lists the bundled OCR files missing from <paramref name="dataDirectory"/>.</summary>
    public static IEnumerable<string> FindMissingFiles(string dataDirectory)
    {
        return RequiredFiles.Where(f => !File.Exists(Path.Combine(dataDirectory, f)));
    }

    /// <summary>Joins the recognized lines of a WeChatOcr result.</summary>
    public static string JoinLines(WeChatOcrResult result)
    {
        var lines = ExtractLines(result);
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.AppendLine(line.Text);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Maps a WeChatOcr result to ordered <see cref="OcrTextLine"/> boxes.</summary>
    public static IReadOnlyList<OcrTextLine> ExtractLines(WeChatOcrResult result)
    {
        var raw = result?.OcrResult?.SingleResult;
        if (raw == null || raw.Count == 0)
        {
            return [];
        }

        var lines = new List<OcrTextLine>(raw.Count);
        foreach (var line in raw)
        {
            if (line == null || string.IsNullOrEmpty(line.SingleStrUtf8))
            {
                continue;
            }

            double left = line.Left;
            double top = line.Top;
            double right = line.Right;
            double bottom = line.Bottom;

            // Prefer polygon bounds when the axis-aligned box looks empty.
            if (right <= left || bottom <= top)
            {
                var pts = line.SinglePos?.Pos;
                if (pts != null && pts.Count > 0)
                {
                    left = pts.Min(p => p.X);
                    top = pts.Min(p => p.Y);
                    right = pts.Max(p => p.X);
                    bottom = pts.Max(p => p.Y);
                }
            }

            if (right <= left || bottom <= top)
            {
                continue;
            }

            IReadOnlyList<Point> corners = null;
            var pos = line.SinglePos?.Pos;
            if (pos != null && pos.Count >= 4)
            {
                corners =
                [
                    new Point(pos[0].X, pos[0].Y),
                    new Point(pos[1].X, pos[1].Y),
                    new Point(pos[2].X, pos[2].Y),
                    new Point(pos[3].X, pos[3].Y),
                ];
            }

            lines.Add(new OcrTextLine(
                line.SingleStrUtf8,
                new Rect(left, top, right - left, bottom - top),
                line.SingleRate,
                corners));
        }

        // Reading order: top-to-bottom, then left-to-right.
        return lines
            .OrderBy(l => l.Box.Top)
            .ThenBy(l => l.Box.Left)
            .ToList();
    }

    /// <summary>User-facing "text extraction failed" message with the technical detail appended.</summary>
    public static string FailedMessage(string detail)
        => $"{Properties.Resources.ocr_err_failed} {detail}";

    private static async Task<IReadOnlyList<OcrTextLine>> RunOcrLinesAsync(byte[] png, CancellationToken cancellationToken)
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"snip-ocr-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(imagePath, png, cancellationToken).ConfigureAwait(false);
            return await RunOcrPathAsync(imagePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(imagePath);
        }
    }

    /// <summary>
    /// Runs one already-written image file through the shared component. The
    /// caller owns the file and its cleanup; the native protocol takes a path.
    /// </summary>
    private static async Task<IReadOnlyList<OcrTextLine>> RunOcrPathAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<OcrTextLine>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Point WeChatOcr at the folder that contains wco_data (plugin or LocalAppData).
            var dataDir = ResolveOcrDataDirectory()
                ?? throw new OcrException(Properties.Resources.ocr_err_need_install);
            var baseDir = Path.GetDirectoryName(dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                ?? PluginDirectory;
            DataLocation.SetBaseDirectory(baseDir);

            // Reuse the OCR component across calls: (re)create only when missing
            // or after a hard failure. Its WeChatOCR.exe child process stays warm.
            if (_sharedOcr == null)
            {
                _sharedOcr = new ImageOcr();
            }

            try
            {
                for (int attempt = 0; attempt < MaxAttempts && !tcs.Task.IsCompleted; attempt++)
                {
                    _sharedOcr.Run(imagePath, (path, result) =>
                    {
                        // WeChatOcr may report progress with a null result; wait for the real one.
                        if (result != null)
                        {
                            tcs.TrySetResult(ExtractLines(result));
                        }
                    });

                    await Task.WhenAny(tcs.Task, Task.Delay(AttemptTimeout, cancellationToken)).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            catch (Exception ex) when (ex is not OcrException && ex is not OperationCanceledException)
            {
                // The component (or its child process) is broken; rebuild next call.
                TryDisposeSharedOcr();
                throw;
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
    }

    private static string PluginDirectory
        => Path.GetDirectoryName(typeof(WeChatOcrService).Assembly.Location) ?? AppContext.BaseDirectory;

    /// <summary>
    /// Disposes the shared OCR component (killing its WeChatOCR.exe child process).
    /// Call on plugin teardown. Safe to call multiple times; a later recognition
    /// simply recreates the component.
    /// </summary>
    public static void ShutdownOcr()
    {
        if (!Gate.Wait(0))
        {
            // A recognition is in flight; let it finish and dispose its own way.
            return;
        }

        try
        {
            TryDisposeSharedOcr();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void TryDisposeSharedOcr()
    {
        var ocr = Interlocked.Exchange(ref _sharedOcr, null);
        try
        {
            ocr?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip WeChat OCR component dispose: {ex.Message}", typeof(WeChatOcrService));
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
