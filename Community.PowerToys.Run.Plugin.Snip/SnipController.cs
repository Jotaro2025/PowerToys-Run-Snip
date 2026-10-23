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
    public static void Start(SnipSettings settings, Action<string, string> notify, CaptureKind kind = CaptureKind.Region)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Dispatcher dispatcher = EnsureUiDispatcher();
        dispatcher.BeginInvoke(new Action(() => _ = RunAsync(settings, notify, kind)), DispatcherPriority.Normal);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the dedicated STA UI dispatcher
    /// (same thread as the snip editor). Returns immediately.
    /// </summary>
    public static void InvokeOnUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureUiDispatcher().BeginInvoke(action, DispatcherPriority.Normal);
    }

    /// <summary>
    /// Runs <paramref name="action"/> synchronously on the dedicated STA UI dispatcher.
    /// </summary>
    public static void InvokeOnUiThreadSync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = EnsureUiDispatcher();
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action, DispatcherPriority.Normal);
    }

    /// <summary>
    /// Shows the dedicated settings window on PowerToys Run's WPF dispatcher.
    /// When the user saves, <paramref name="onSaved"/> runs on that same dispatcher
    /// (hotkey re-registration still marshals to the snip STA via <see cref="InvokeOnUiThread"/>).
    /// </summary>
    /// <remarks>
    /// Settings must NOT use the dedicated snip capture STA. That thread owns overlay /
    /// clipboard Freezables; creating a settings <see cref="System.Windows.Window"/> there
    /// while Run's mouse/input runs on <c>Application.Current.Dispatcher</c> causes
    /// <see cref="System.InvalidOperationException"/> (Freezable/style seal) during mouse-over.
    /// Screenshot / editor flow stays on the dedicated STA via <see cref="Start"/>.
    /// </remarks>
    public static void ShowSettingsWindow(SnipSettings settings, Action onSaved)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Prefer the host (PowerToys Run) dispatcher so styles/Freezables share affinity
        // with Run's mouse input. BeginInvoke so Result.Action can return and Run can hide.
        var host = System.Windows.Application.Current?.Dispatcher;
        if (host != null)
        {
            host.BeginInvoke(
                new Action(() => ShowSettingsWindowCore(settings, onSaved, setOwner: true)),
                DispatcherPriority.Normal);
            return;
        }

        // Unit tests / headless: no WPF Application — fall back to dedicated STA.
        InvokeOnUiThread(() => ShowSettingsWindowCore(settings, onSaved, setOwner: false));
    }

    private static void ShowSettingsWindowCore(SnipSettings settings, Action onSaved, bool setOwner)
    {
        UiLanguageHelper.Apply(settings.UiLanguage);
        var window = new SnipSettingsWindow(settings);
        if (setOwner)
        {
            // Only set Owner on the same thread as MainWindow (never cross-dispatcher).
            var main = System.Windows.Application.Current?.MainWindow;
            if (main != null && main.Dispatcher.CheckAccess())
            {
                window.Owner = main;
            }
        }

        var ok = window.ShowDialog() == true;
        if (ok)
        {
            onSaved?.Invoke();
        }
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

    private static async Task RunAsync(SnipSettings settings, Action<string, string> notify, CaptureKind kind)
    {
        System.Windows.Media.Imaging.WriteableBitmap bitmap = null;
        try
        {
            // Give PowerToys Run time to hide its window before the capture.
            // Visible topmost countdown when delay > 0 (same STA as the editor).
            if (settings.CaptureDelayMs > 0)
            {
                await CaptureCountdownWindow.RunAsync(settings.CaptureDelayMs).ConfigureAwait(true);
            }

            var capture = CaptureFactory.Create();
            var screens = capture.GetScreens();
            System.Windows.Rect? initialSelection = null;
            Core.ScreenInfo screen;
            System.Collections.Generic.IReadOnlyList<Core.ScreenInfo> monitors = Win32CaptureService.PhysicalScreens(screens);

            if (kind == CaptureKind.ActiveWindow)
            {
                if (!Win32CaptureService.TryGetForegroundWindowBounds(out var phys))
                {
                    notify(Properties.Resources.msg_failed, Properties.Resources.msg_no_active_window);
                    return;
                }

                var host = Win32CaptureService.GetScreenAtPhysicalPoint(
                    screens,
                    new System.Windows.Point(phys.X + (phys.Width / 2.0), phys.Y + (phys.Height / 2.0)));
                double dpi = host.DpiScale > 0 ? host.DpiScale : 1.0;
                screen = new Core.ScreenInfo(
                    IntPtr.Zero,
                    "ActiveWindow",
                    new System.Windows.Rect(phys.X, phys.Y, phys.Width, phys.Height),
                    new System.Windows.Rect(phys.X, phys.Y, phys.Width, phys.Height),
                    dpi,
                    false);

                using (var frame = await Task.Run(() => capture.CaptureScreenRectPixels(phys.X, phys.Y, phys.Width, phys.Height)).ConfigureAwait(true))
                {
                    bitmap = frame.ToWriteableBitmap();
                }

                initialSelection = new System.Windows.Rect(0, 0, phys.Width / dpi, phys.Height / dpi);
                monitors = [];
            }
            else
            {
                // One monitor: that monitor (unchanged). Several: whole virtual desktop so the
                // selection may span monitors (physical pixels; overlay maps DIP via its window DPI).
                screen = Win32CaptureService.ChooseCaptureTarget(screens);

                // GDI BitBlt off the UI STA (owned DIB, no mid-frame byte[]); Span-copy to WB on STA.
                using (var frame = await Task.Run(() => capture.CaptureScreenPixels(screen)).ConfigureAwait(true))
                {
                    bitmap = frame.ToWriteableBitmap();
                }

                if (kind == CaptureKind.FullScreen)
                {
                    double dpi = screen.DpiScale > 0 ? screen.DpiScale : 1.0;
                    initialSelection = new System.Windows.Rect(0, 0, screen.Bounds.Width / dpi, screen.Bounds.Height / dpi);
                }
            }

            await RunEditorAsync(bitmap, screen, monitors, settings, notify, initialSelection).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip capture failed: {ex.Message}", ex, typeof(SnipController));
            notify(Properties.Resources.msg_failed, ex.Message);
        }
    }

    private static async Task RunEditorAsync(
        System.Windows.Media.Imaging.WriteableBitmap bitmap,
        Core.ScreenInfo screen,
        System.Collections.Generic.IReadOnlyList<Core.ScreenInfo> monitors,
        SnipSettings settings,
        Action<string, string> notify,
        System.Windows.Rect? initialSelection = null)
    {
        var request = new EditRequest { InitialSelection = initialSelection };
        var window = new SnipWindow(bitmap, screen, request, settings, monitors);
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
                await TryCopyAsync(result.Result, result.PngBytes).ConfigureAwait(true);
            }

            notify(Properties.Resources.msg_saved, result.SavedPath);
            return;
        }

        // Confirmed: apply the user's clipboard / auto-save preferences.
        if (settings.CopyAfterCapture)
        {
            await TryCopyAsync(result.Result, result.PngBytes).ConfigureAwait(true);
        }

        if (settings.AutoSaveAfterCapture)
        {
            var (ok, path, error) = await ScreenshotFileSaver.SavePngAsync(
                result.PngBytes,
                settings.SaveDirectory,
                SnipSettings.DefaultSaveFolder).ConfigureAwait(true);
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

    /// <summary>Large frames: CopyPixels/DIB build off the UI thread via <see cref="ClipboardService.CopyBitmapAsync"/>.</summary>
    private static async Task TryCopyAsync(System.Windows.Media.Imaging.BitmapSource bitmap, byte[] pngBytes = null)
    {
        if (bitmap == null)
        {
            return;
        }

        try
        {
            await ClipboardService.CopyBitmapAsync(bitmap, pngBytes).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip clipboard copy failed: {ex.Message}", ex, typeof(SnipController));
        }
    }
}
