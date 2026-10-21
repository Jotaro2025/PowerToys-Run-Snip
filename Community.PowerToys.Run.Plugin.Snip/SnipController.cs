// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Windows.Threading;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Community.PowerToys.Run.Plugin.Snip.UI;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip;

/// <summary>
/// Runs a screenshot action on a single dedicated STA UI thread. PowerToys Run
/// invokes <see cref="Wox.Plugin.Result.Action"/> synchronously and hides its
/// window only after the action returns, so the capture is deferred: the thread
/// waits for the Run window to disappear, captures, and then either shows the
/// editor or copies/saves directly.
/// </summary>
/// <remarks>
/// Everything WPF (capture bitmap, overlay window, toolbar, clipboard) happens
/// on this one thread, mirroring how a single-UI-thread app hosts the editor.
/// Sharing brushes, cursors or bitmaps across several short-lived threads makes
/// WPF throw cross-thread Freezable/DependencyObject exceptions.
/// </remarks>
public static class SnipController
{
    private static readonly object Gate = new object();
    private static Dispatcher _uiDispatcher;

    /// <summary>
    /// Kicks off a screenshot. Returns immediately; <paramref name="notify"/> is
    /// invoked when the action finishes (success or failure).
    /// </summary>
    public static void Start(SnipMode mode, SnipSettings settings, Action<string, string> notify)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Dispatcher dispatcher = EnsureUiDispatcher();
        dispatcher.BeginInvoke(new Action(() => Run(mode, settings, notify)), DispatcherPriority.Normal);
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

    private static void Run(SnipMode mode, SnipSettings settings, Action<string, string> notify)
    {
        System.Windows.Media.Imaging.WriteableBitmap bitmap = null;
        try
        {
            // Give PowerToys Run time to hide its window before the capture.
            if (settings.CaptureDelayMs > 0)
            {
                Thread.Sleep(settings.CaptureDelayMs);
            }

            var capture = CaptureFactory.Create();
            var screens = capture.GetScreens();
            var screen = Win32CaptureService.GetScreenAtCursor(screens);
            bitmap = capture.CaptureScreen(screen);

            switch (mode)
            {
                case SnipMode.Region:
                    RunEditor(bitmap, screen, settings, notify);
                    return;

                case SnipMode.FullScreenClipboard:
                    ClipboardService.CopyBitmap(bitmap);
                    notify(Properties.Resources.msg_full_copied, string.Empty);
                    return;

                case SnipMode.FullScreenSave:
                    RunFullScreenSave(bitmap, settings, notify);
                    return;
            }
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
        var window = new SnipWindow(bitmap, screen, new EditRequest());
        window.ShowDialog();

        var result = window.Result;
        if (result == null || result.Outcome == EditOutcome.Cancelled || result.PngBytes == null)
        {
            return;
        }

        if (result.Outcome == EditOutcome.ExtractText)
        {
            ShowTextExtraction(result, notify);
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
                TryCopy(result.Result);
            }

            notify(Properties.Resources.msg_saved, result.SavedPath);
            return;
        }

        // Confirmed: apply the user's clipboard / auto-save preferences.
        if (settings.CopyAfterCapture)
        {
            TryCopy(result.Result);
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

    /// <summary>
    /// Opens the (modeless) text extraction window on this UI thread; it runs
    /// the OCR itself and stays open after this action returns.
    /// </summary>
    private static void ShowTextExtraction(EditResult result, Action<string, string> notify)
    {
        try
        {
            var window = new OcrResultWindow(result.Result, result.PngBytes);
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip text extraction window failed: {ex.Message}", ex, typeof(SnipController));
            notify(Properties.Resources.ocr_window_title, ex.Message);
        }
    }

    private static void RunFullScreenSave(
        System.Windows.Media.Imaging.WriteableBitmap bitmap,
        SnipSettings settings,
        Action<string, string> notify)
    {
        var png = PngEncoder.Encode(bitmap);
        var (ok, path, error) = ScreenshotFileSaver.SavePngAsync(
            png,
            settings.SaveDirectory,
            SnipSettings.DefaultSaveFolder).GetAwaiter().GetResult();
        if (ok)
        {
            notify(Properties.Resources.msg_full_saved, path);
        }
        else
        {
            notify(Properties.Resources.msg_failed, error);
        }
    }

    private static void TryCopy(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        if (bitmap == null)
        {
            return;
        }

        try
        {
            ClipboardService.CopyBitmap(bitmap);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip clipboard copy failed: {ex.Message}", ex, typeof(SnipController));
        }
    }
}
