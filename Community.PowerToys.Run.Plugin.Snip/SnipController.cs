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
/// Runs a region screenshot on a single dedicated STA UI thread. PowerToys Run
/// invokes <see cref="Wox.Plugin.Result.Action"/> synchronously and hides its
/// window only after the action returns, so the capture is deferred: the thread
/// waits for the Run window to disappear, captures, then shows the editor.
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
    public static void Start(SnipSettings settings, Action<string, string> notify)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Dispatcher dispatcher = EnsureUiDispatcher();
        dispatcher.BeginInvoke(new Action(() => Run(settings, notify)), DispatcherPriority.Normal);
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

    private static void Run(SnipSettings settings, Action<string, string> notify)
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

        if (result.Outcome == EditOutcome.TranslateImage)
        {
            ShowImageTranslate(result, settings, notify);
            return;
        }

        if (result.Outcome == EditOutcome.Pinned)
        {
            ShowPinnedImage(result, notify);
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

    /// <summary>
    /// Opens the (modeless) image-translate window on this UI thread.
    /// </summary>
    private static void ShowImageTranslate(EditResult result, SnipSettings settings, Action<string, string> notify)
    {
        try
        {
            var window = new TranslateResultWindow(result.Result, result.PngBytes, settings);
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip image translate window failed: {ex.Message}", ex, typeof(SnipController));
            notify(Properties.Resources.tr_window_title, ex.Message);
        }
    }

    /// <summary>Opens a modeless topmost pin window for the captured bitmap.</summary>
    private static void ShowPinnedImage(EditResult result, Action<string, string> notify)
    {
        try
        {
            var window = new PinImageWindow(result.Result);
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip pin window failed: {ex.Message}", ex, typeof(SnipController));
            notify(Properties.Resources.pin_window_title, ex.Message);
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
