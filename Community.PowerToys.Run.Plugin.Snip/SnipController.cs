// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Community.PowerToys.Run.Plugin.Snip.UI;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip;

/// <summary>
/// Runs a region screenshot on a single dedicated STA UI thread. PowerToys Run
/// invokes <see cref="Wox.Plugin.Result.Action"/> synchronously and hides its
/// window only after the action returns, so the capture is deferred: the thread
/// waits for the Run window to disappear, captures, then shows the editor.
/// </summary>
/// <remarks>
/// Heavy GDI capture and PNG encode run on the thread pool; WPF bitmaps,
/// overlay window, toolbar and clipboard stay on this one STA thread so
/// Freezable/DependencyObject affinity is preserved.
/// </remarks>
public static class SnipController
{
    private static readonly object Gate = new object();
    private static Dispatcher _uiDispatcher;

    /// <summary>
    /// Kicks off a screenshot. Returns immediately; <paramref name="notify"/> is
    /// invoked when the action finishes (success or failure).
    /// </summary>
    public static void Start(SnipSettings settings, Action<string, string> notify)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Dispatcher dispatcher = EnsureUiDispatcher();
        dispatcher.BeginInvoke(new Action(() => _ = RunAsync(settings, notify)), DispatcherPriority.Normal);
    }

    private static Dispatcher EnsureUiDispatcher()
    {
        if (_uiDispatcher != null)
        {
            return _uiDispatcher;
        }

        lock (Gate)
        {
            if (_uiDispatcher != null)
            {
                return _uiDispatcher;
            }

            using var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                // Dispatcher.CurrentDispatcher creates this thread's dispatcher.
                _uiDispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            ready.Wait();
            return _uiDispatcher;
        }
    }

    private static async Task RunAsync(SnipSettings settings, Action<string, string> notify)
    {
        System.Windows.Media.Imaging.WriteableBitmap bitmap = null;
        try
        {
            // Give PowerToys Run time to hide its window before the capture.
            if (settings.CaptureDelayMs > 0)
            {
                await Task.Delay(settings.CaptureDelayMs).ConfigureAwait(true);
            }

            var capture = CaptureFactory.Create();
            var screens = capture.GetScreens();
            var screen = Win32CaptureService.GetScreenAtCursor(screens);

            // GDI BitBlt off the UI STA (owned DIB, no mid-frame byte[]); Span-copy to WB on STA.
            using (var frame = await Task.Run(() => capture.CaptureScreenPixels(screen)).ConfigureAwait(true))
            {
                bitmap = frame.ToWriteableBitmap();
            }

            RunEditor(bitmap, screen, settings, notify);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip capture failed: {ex.Message}", ex, typeof(SnipController));
            notify(Properties.Resources.msg_failed, ex.Message);
        }
    }

    private static void RunEditor(
        System.Windows.Media.Imaging.WriteableBitmap bitmap,
        Core.ScreenInfo screen,
        SnipSettings settings,
        Action<string, string> notify)
    {
        var window = new SnipWindow(bitmap, screen, new EditRequest(), settings);
        window.ShowDialog();

        // OCR / translate / pin are opened as session children inside SnipWindow
        // (overlay hide → result → restore). Only finishing outcomes reach here.
        var result = window.Result;
        if (result == null || result.Outcome == EditOutcome.Cancelled || result.PngBytes == null)
        {
            return;
        }

        if (result.Outcome == EditOutcome.Copied)
        {
            // The editor already copied to the clipboard.
            return;
        }

        if (result.Outcome == EditOutcome.Saved)
        {
            if (settings.CopyAfterCapture)
            {
                TryCopy(result.Result, result.PngBytes);
            }

            notify(Properties.Resources.msg_saved, result.SavedPath);
            return;
        }

        // Confirmed: apply the user's clipboard / auto-save preferences.
        if (settings.CopyAfterCapture)
        {
            TryCopy(result.Result, result.PngBytes);
        }

        if (settings.AutoSaveAfterCapture)
        {
            var (ok, path, error) = ScreenshotFileSaver.SavePngAsync(
                result.PngBytes,
                settings.SaveDirectory,
                SnipSettings.DefaultSaveFolder).GetAwaiter().GetResult();
            if (ok)
            {
                notify(Properties.Resources.msg_saved, path);
                return;
            }

            notify(Properties.Resources.msg_failed, error);
            return;
        }

        if (settings.CopyAfterCapture)
        {
            notify(Properties.Resources.msg_copied, string.Empty);
        }
    }

    private static void TryCopy(System.Windows.Media.Imaging.BitmapSource bitmap, byte[] pngBytes = null)
    {
        if (bitmap == null)
        {
            return;
        }

        try
        {
            ClipboardService.CopyBitmap(bitmap, pngBytes);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip clipboard copy failed: {ex.Message}", ex, typeof(SnipController));
        }
    }
}
