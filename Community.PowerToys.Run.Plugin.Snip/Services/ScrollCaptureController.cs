// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Adapted from LdotJdot/TDS (MIT) ScrollCaptureController for WPF/Snip.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Community.PowerToys.Run.Plugin.Snip;
using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.UI;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Automatic scroll-capture loop: capture tile → SendWheel at selection center → delay → repeat.
/// Stitching happens once on confirm/save via <see cref="ScrollStitcher"/>.
/// Auto-stops after consecutive near-identical tiles (page reached the end).
/// </summary>
public sealed class ScrollCaptureController
{
    /// <summary>Stop when this many consecutive full-duplicate tiles are seen.</summary>
    internal const int ConsecutiveDuplicateStopThreshold = 4;

    /// <summary>Ignore duplicate streak until this many wheels have succeeded (Edge settle).</summary>
    internal const int MinSuccessfulWheelsBeforeDuplicateStop = 5;

    /// <summary>Extra settle after wheel before capture for the first few tiles (ms).</summary>
    internal const int EarlyTileExtraSettleMs = 250;

    /// <summary>Stop when this many consecutive wheel deliveries fail (no underlay HWND).</summary>
    internal const int ConsecutiveWheelFailStopThreshold = 5;

    private readonly ICaptureService _capture;
    private readonly Panel _overlayHost;
    private readonly ScrollHintOverlay _overlay;
    private readonly Int32Rect _captureRect;
    private readonly IntPtr _overlayHwnd;
    private readonly int _maxTiles;
    private readonly int _scrollIntervalMs;
    private readonly int _stepsPerTick;
    private readonly bool _scrollUp;
    private readonly Dispatcher _dispatcher;

    private readonly List<ScrollTilePixels> _tiles = new();
    private CancellationTokenSource _cts;
    private bool _active;
    private int _captureBusy;
    private bool _stitchingOverlayActive;
    private bool _stoppedPending;
    private int _stopNotified;
    private bool _heightCapReached;
    private int _maxHeightPx;
    private volatile bool _paused;
    private ScrollCaptureStopGuard _stopGuard;
    private bool _sessionOverlayHidden;
    private IntPtr _windowCaptureHwnd;
    private bool _preferWindowCapture;
    private int _windowCaptureFailStreak;

    public event Action<int> CapturingStopped;

    public event Action Cancelled;

    public bool IsActive => _active;

    public int TileCount => _tiles.Count;

    public bool HasTiles => _tiles.Count > 0;

    /// <summary>True after capture stopped (manual/auto) while tiles are kept for stitch.</summary>
    public bool IsStoppedPending => _stoppedPending && _tiles.Count > 0;

    /// <summary>True when capture auto-stopped because the projected stitched height hit the cap.</summary>
    public bool HeightCapReached => _heightCapReached;

    /// <summary>True when this session scrolls content upward (tiles are reversed before stitch).</summary>
    public bool ScrollUp => _scrollUp;

    /// <summary>True while capture is paused because the cursor left the selection.</summary>
    public bool IsPaused => _paused;

    public ScrollCaptureController(
        ICaptureService capture,
        Panel overlayHost,
        Int32Rect captureRect,
        IntPtr overlayHwnd = default,
        int scrollIntervalMs = 200,
        int stepsPerTick = 1,
        bool scrollUp = false,
        int maxTiles = 200)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _overlayHost = overlayHost ?? throw new ArgumentNullException(nameof(overlayHost));
        _captureRect = captureRect;
        _overlayHwnd = overlayHwnd;
        _scrollIntervalMs = scrollIntervalMs;
        _stepsPerTick = stepsPerTick;
        _scrollUp = scrollUp;
        _maxTiles = maxTiles;
        _overlay = new ScrollHintOverlay();
        _dispatcher = overlayHost.Dispatcher;
    }

    /// <summary>
    /// Optional area (overlay-host DIP) to center the scroll hint in, e.g. the monitor that holds
    /// the selection on a multi-monitor overlay. Null = center across the whole host.
    /// </summary>
    public Rect? OverlayArea { get; set; }

    /// <summary>
    /// Begin auto-scroll capture. Caller must hole-punch and hide adorners first.
    /// </summary>
    public void Begin()
    {
        if (_active)
        {
            return;
        }

        _active = true;
        _paused = false;
        _stoppedPending = false;
        _heightCapReached = false;
        Interlocked.Exchange(ref _stopNotified, 0);
        Interlocked.Exchange(ref _captureBusy, 0);
        _tiles.Clear();

        ShowOverlay();
        PostToOverlay(() => _overlay.ShowManualCapturing(0));
        SnipVerbose.Scroll("[scroll] auto-scroll started");

        _cts = new CancellationTokenSource();
        StartStopGuard();
        _ = AutoScrollLoopAsync(_cts.Token);
    }

    private async Task AutoScrollLoopAsync(CancellationToken ct)
    {
        int consecutiveDups = 0;
        int consecutiveWheelFails = 0;
        int successfulWheels = 0;
        int projectedHeight = 0;
        int maxHeight = int.MaxValue;
        int scrollHint = 0;
        string autoStopReason = null;

        // Prefer window-local capture (WinRT CreateForWindow / PrintWindow) so the
        // overlay can stay visible (selection hole + stop strip). Fall back to
        // session SW_HIDE + desktop BitBlt when window capture is unavailable —
        // required for Edge reliability (#89/#90).
        ProbeWindowCapturePreference();
        if (!_preferWindowCapture)
        {
            BeginSessionOverlayHide();
            Log.Info(
                "Snip scroll: overlay session SW_HIDE for Edge wheel/capture (window-capture unavailable)",
                typeof(ScrollCaptureController));
        }
        else
        {
            Log.Info(
                $"Snip scroll: prefer window capture hwnd=0x{_windowCaptureHwnd.ToInt64():X} (overlay stays visible)",
                typeof(ScrollCaptureController));
        }

        try
        {
            var first = await CaptureOnceSafeAsync().ConfigureAwait(false);
            if (first != null && _active && !ct.IsCancellationRequested)
            {
                _tiles.Add(first);
                projectedHeight = first.Height;
                maxHeight = ScrollStitcher.ResolveMaxStitchedHeight(first.Width);
                _stopGuard?.SetTileCount(_tiles.Count);
            }
            else if (first == null)
            {
                Log.Warn("Snip scroll: first tile capture failed", typeof(ScrollCaptureController));
            }

            // Focus underlay + hold cursor while overlay is hidden so the click hits Edge.
            BeginPointerHoldAtCenter();
            MaybeWarnElevatedTarget();

            // Mouse guard arms once the cursor has been inside the selection, so starting
            // from the toolbar (outside the selection) does not pause immediately.
            bool guardArmed = false;
            while (!ct.IsCancellationRequested && _active && _tiles.Count < _maxTiles)
            {
                if (_stopGuard != null && _stopGuard.PollStopKeys())
                {
                    break;
                }

                // Programmatic hold keeps the cursor at selection center; do not false-pause.
                bool inside = Win32CaptureService.IsScrollPointerHeld || IsCursorInsideCapture();
                guardArmed |= inside;
                if (guardArmed && !inside)
                {
                    if (!_paused)
                    {
                        _paused = true;
                        PostToOverlay(() => _overlay.ShowPaused());
                        _stopGuard?.SetPaused(true);
                        SnipVerbose.Scroll("[scroll] paused — cursor left selection");
                    }

                    if (!await DelayCancelableAsync(_scrollIntervalMs, ct).ConfigureAwait(false))
                    {
                        break;
                    }

                    continue;
                }

                if (_paused)
                {
                    _paused = false;
                    _stopGuard?.SetPaused(false);
                    SnipVerbose.Scroll("[scroll] resumed — cursor back in selection");
                }

                PostToOverlay(() => _overlay.ShowCapturing(_tiles.Count + 1));

                bool wheeled = SimulateWheelAtCenter();
                if (wheeled)
                {
                    consecutiveWheelFails = 0;
                    successfulWheels++;
                }
                else
                {
                    consecutiveWheelFails++;
                    if (consecutiveWheelFails >= ConsecutiveWheelFailStopThreshold)
                    {
                        autoStopReason = $"wheel target unresolved after {consecutiveWheelFails} failures";
                        Log.Warn(
                            $"Snip scroll auto-stop: {autoStopReason} ({_tiles.Count} tile(s))",
                            typeof(ScrollCaptureController));
                        break;
                    }
                }

                int settleMs = _scrollIntervalMs;
                if (successfulWheels > 0 && successfulWheels <= MinSuccessfulWheelsBeforeDuplicateStop)
                {
                    settleMs += EarlyTileExtraSettleMs;
                }

                if (!await DelayCancelableAsync(settleMs, ct).ConfigureAwait(false))
                {
                    break;
                }

                // Capture while overlay stays session-hidden so BitBlt reads live Edge.
                ScrollTilePixels bmp = await CaptureOnceSafeAsync().ConfigureAwait(false);

                if (bmp == null || ct.IsCancellationRequested || !_active)
                {
                    break;
                }

                bool duplicate = false;
                if (_tiles.Count > 0)
                {
                    duplicate = ScrollStitcher.IsNearlyIdentical(_tiles[_tiles.Count - 1], bmp);
                }

                if (duplicate)
                {
                    bmp.Dispose();

                    // Failed wheels produce identical tiles; never count those toward end-of-page stop.
                    if (!ShouldCountDuplicateTowardAutoStop(wheeled, successfulWheels))
                    {
                        consecutiveDups = 0;
                        SnipVerbose.Scroll("[scroll] duplicate after failed wheel — streak not counted");
                        continue;
                    }

                    consecutiveDups++;
                    SnipVerbose.Scroll($"[scroll] duplicate tile (streak={consecutiveDups}) — not kept");
                    if (consecutiveDups >= ConsecutiveDuplicateStopThreshold)
                    {
                        autoStopReason =
                            $"page no longer scrolling (dup streak={consecutiveDups}/{ConsecutiveDuplicateStopThreshold}, wheels={successfulWheels})";
                        Log.Warn(
                            $"Snip scroll auto-stop: {autoStopReason} ({_tiles.Count} tile(s))",
                            typeof(ScrollCaptureController));
                        SnipVerbose.Scroll($"[scroll] auto-stop — {autoStopReason}");
                        break;
                    }

                    continue;
                }

                consecutiveDups = 0;

                // Scroll-up: newest tile sits on top, so measure older-below-newer (same row count).
                int contributed = _tiles.Count == 0
                    ? bmp.Height
                    : _scrollUp
                        ? ScrollStitcher.EstimateContributedRows(bmp, _tiles[_tiles.Count - 1], scrollHint)
                        : ScrollStitcher.EstimateContributedRows(_tiles[_tiles.Count - 1], bmp, scrollHint);
                if (contributed > 0 && contributed < bmp.Height)
                {
                    scrollHint = contributed;
                }

                _tiles.Add(bmp);
                projectedHeight += contributed;
                PostToOverlay(() => _overlay.ShowManualCapturing(_tiles.Count));
                _stopGuard?.SetTileCount(_tiles.Count);
                SnipVerbose.Scroll($"[scroll] tile #{_tiles.Count} captured (projected h={projectedHeight}/{maxHeight})");

                if (projectedHeight >= maxHeight)
                {
                    _heightCapReached = true;
                    _maxHeightPx = maxHeight;
                    autoStopReason = $"stitched height cap ({maxHeight}px)";
                    Log.Warn(
                        $"Snip scroll auto-stop: {autoStopReason} ({_tiles.Count} tile(s))",
                        typeof(ScrollCaptureController));
                    SnipVerbose.Scroll("[scroll] auto-stop — stitched height cap reached");
                    break;
                }
        }

            if (autoStopReason == null && !ct.IsCancellationRequested && _tiles.Count >= _maxTiles)
            {
                autoStopReason = $"max tiles ({_maxTiles})";
                Log.Warn(
                    $"Snip scroll auto-stop: {autoStopReason} ({_tiles.Count} tile(s))",
                    typeof(ScrollCaptureController));
            }
        }
        finally
        {
            // Strip first, then unhide overlay — avoids z-order race (strip over restored
            // snip / toolbar flash) when mid-stop and loop exit interleave.
            RestoreAfterScrollSession();
        }

        _active = false;
        await FinishCapturingUiAsync(autoStopped: !ct.IsCancellationRequested).ConfigureAwait(false);
    }

    /// <summary>
    /// Duplicate tiles only count toward end-of-page auto-stop after enough successful
    /// wheels (Chromium often needs a few ticks before pixels move) and when the latest
    /// wheel also succeeded.
    /// </summary>
    internal static bool ShouldCountDuplicateTowardAutoStop(bool lastWheelSucceeded, int successfulWheelCount)
        => lastWheelSucceeded && successfulWheelCount >= MinSuccessfulWheelsBeforeDuplicateStop;

    private void StartStopGuard()
    {
        DisposeStopGuard();
        try
        {
            double dpi = 1.0;
            try
            {
                dpi = _dispatcher.Invoke(() =>
                {
                    var src = PresentationSource.FromVisual(_overlayHost);
                    return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                });
            }
            catch (Exception)
            {
                dpi = 1.0;
            }

            _stopGuard = new ScrollCaptureStopGuard(
                onStop: RequestManualStopFromGuard,
                capturePhysicalPx: _captureRect,
                dpiScale: dpi,
                dispatcher: _dispatcher);
            _stopGuard.Start();
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip scroll stop guard start failed: {ex.Message}", ex, typeof(ScrollCaptureController));
            _stopGuard = null;
        }
    }

    private void RequestManualStopFromGuard()
    {
        try
        {
            _cts?.Cancel();
            _active = false;
            SnipVerbose.Scroll("[scroll] stop guard cancelled CTS (keep tiles)");
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll stop guard cancel failed: {ex.Message}", typeof(ScrollCaptureController));
        }
    }

    private void DisposeStopGuard()
    {
        var g = _stopGuard;
        _stopGuard = null;
        try
        {
            g?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll stop guard dispose failed: {ex.Message}", typeof(ScrollCaptureController));
        }
    }

    /// <summary>
    /// Ordered teardown after scroll: close stop strip → restore snip HWND → release pointer hold.
    /// Call from loop finally, Stop, and Cancel so mid-stop and auto-stop share one path.
    /// </summary>
    private void RestoreAfterScrollSession()
    {
        DisposeStopGuard();
        EndSessionOverlayHide();
        EndPointerHold();
    }

    private void BeginSessionOverlayHide()
    {
        if (_overlayHwnd == IntPtr.Zero || _sessionOverlayHidden)
        {
            return;
        }

        Win32CaptureService.BeginScrollSessionOverlayHide(_overlayHwnd);
        _sessionOverlayHidden = true;
    }

    private void EndSessionOverlayHide()
    {
        if (!_sessionOverlayHidden)
        {
            return;
        }

        _sessionOverlayHidden = false;
        try
        {
            Win32CaptureService.EndScrollSessionOverlayHide(_overlayHwnd);
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll EndScrollSessionOverlayHide failed: {ex.Message}", typeof(ScrollCaptureController));
        }
    }

    /// <summary>
    /// Delay in short slices so Esc/Space mid-stop is responsive during settle.
    /// Returns false when cancelled or stop key fired.
    /// </summary>
    private async Task<bool> DelayCancelableAsync(int totalMs, CancellationToken ct)
    {
        int remaining = Math.Max(0, totalMs);
        while (remaining > 0)
        {
            if (ct.IsCancellationRequested || !_active)
            {
                return false;
            }

            if (_stopGuard != null && _stopGuard.PollStopKeys())
            {
                return false;
            }

            int slice = Math.Min(40, remaining);
            try
            {
                await Task.Delay(slice, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return false;
            }

            remaining -= slice;
        }

        return !ct.IsCancellationRequested && _active;
    }

    private bool IsCursorInsideCapture()
    {
        if (!Win32.GetCursorPos(out var pt))
        {
            return true;
        }

        return IsPointInside(_captureRect, pt.X, pt.Y);
    }

    /// <summary>Half-open hit test in physical pixels (right/bottom edge excluded).</summary>
    internal static bool IsPointInside(Int32Rect rect, int x, int y)
        => rect.Width > 0 && rect.Height > 0
            && x >= rect.X && x < rect.X + rect.Width
            && y >= rect.Y && y < rect.Y + rect.Height;

    /// <summary>
    /// Scroll-up sessions capture newest content on top; reverse so the stitcher
    /// always sees a top-to-bottom sequence.
    /// </summary>
    internal static T[] OrderTilesForStitch<T>(IReadOnlyList<T> tiles, bool scrollUp)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var arr = new T[tiles.Count];
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i] = tiles[scrollUp ? arr.Length - 1 - i : i];
        }

        return arr;
    }

    private bool SimulateWheelAtCenter()
    {
        int cx = _captureRect.X + (_captureRect.Width / 2);
        int cy = _captureRect.Y + (_captureRect.Height / 2);
        int delta = (_scrollUp ? 1 : -1) * Win32.WheelDelta * _stepsPerTick;

        // Keep cursor over the hole for the whole session (Chromium needs it).
        if (!Win32CaptureService.IsScrollPointerHeld)
        {
            BeginPointerHoldAtCenter();
        }

        if (Win32CaptureService.TrySendWheelAt(cx, cy, delta, _overlayHwnd))
        {
            return true;
        }

        // SendInput / message path failed — try UIA ScrollPattern on the UI STA.
        if (TryUiaScrollAt(cx, cy, delta))
        {
            SnipVerbose.Scroll($"[scroll] SimulateWheel via UIA ScrollPattern at ({cx},{cy})");
            return true;
        }

        Log.Warn(
            $"Snip scroll SimulateWheel failed at ({cx},{cy}) — SendInput/UIA unavailable",
            typeof(ScrollCaptureController));
        SnipVerbose.Scroll($"[scroll] SimulateWheel failed at ({cx},{cy})");
        return false;
    }

    private bool TryUiaScrollAt(int physicalX, int physicalY, int delta)
    {
        try
        {
            if (_dispatcher.CheckAccess())
            {
                return UiaScrollFallback.TryScrollAt(physicalX, physicalY, delta);
            }

            return _dispatcher.Invoke(() => UiaScrollFallback.TryScrollAt(physicalX, physicalY, delta));
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll UIA invoke failed: {ex.Message}", typeof(ScrollCaptureController));
            return false;
        }
    }

    /// <summary>
    /// Tip when the underlay HWND is higher integrity than this process (UIPI blocks SendInput).
    /// Overlay is usually session-hidden; the stop strip carries the lasting tip.
    /// </summary>
    private void MaybeWarnElevatedTarget()
    {
        try
        {
            int cx = _captureRect.X + (_captureRect.Width / 2);
            int cy = _captureRect.Y + (_captureRect.Height / 2);
            var pt = new Win32.POINT { X = cx, Y = cy };
            IntPtr target = Win32CaptureService.ResolveScrollTargetAt(pt, _overlayHwnd);
            if (target == IntPtr.Zero || !ProcessIntegrity.IsHigherThanCurrent(target))
            {
                return;
            }

            PostToOverlay(() => _overlay.ShowUipiWarning());
            _stopGuard?.SetUipiWarning(true);
            SnipVerbose.Scroll("[scroll] UIPI: underlay higher integrity than host - tip shown");
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll UIPI check failed: {ex.Message}", typeof(ScrollCaptureController));
        }
    }

    private void BeginPointerHoldAtCenter()
    {
        int cx = _captureRect.X + (_captureRect.Width / 2);
        int cy = _captureRect.Y + (_captureRect.Height / 2);
        try
        {
            Win32CaptureService.BeginScrollPointerHold(
                cx,
                cy,
                injectFocusClick: true,
                skipOverlayHwnd: _overlayHwnd);
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll BeginScrollPointerHold failed: {ex.Message}", typeof(ScrollCaptureController));
        }
    }

    private static void EndPointerHold()
    {
        try
        {
            Win32CaptureService.EndScrollPointerHold();
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll EndScrollPointerHold failed: {ex.Message}", typeof(ScrollCaptureController));
        }
    }

    public async Task StopCapturingAsync()
    {
        if (!_active && _cts == null && !_stoppedPending)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _active = false;
            RestoreAfterScrollSession();
            SnipVerbose.Scroll($"[scroll] stopped — {_tiles.Count} raw tile(s) kept");
            await FinishCapturingUiAsync(autoStopped: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip scroll stop failed: {ex.Message}", ex, typeof(ScrollCaptureController));
            _active = false;
            try
            {
                RestoreAfterScrollSession();
            }
            catch (Exception restoreEx)
            {
                Log.Warn($"Snip scroll stop restore failed: {restoreEx.Message}", typeof(ScrollCaptureController));
            }

            await FinishCapturingUiAsync(autoStopped: false).ConfigureAwait(false);
        }
    }

    /// <summary>Show stopped hint (keep overlay) and raise CapturingStopped once.</summary>
    private async Task FinishCapturingUiAsync(bool autoStopped)
    {
        // Idempotent: loop finally / Stop may have already restored.
        RestoreAfterScrollSession();
        if (_tiles.Count > 0)
        {
            _stoppedPending = true;
            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    EnsureOverlayAttached();
                    _overlay.Visibility = Visibility.Visible;
                    if (_heightCapReached)
                    {
                        _overlay.ShowHeightCapStopped(_maxHeightPx);
                    }
                    else
                    {
                        _overlay.ShowManualStopped(_tiles.Count);
                    }

                    LayoutOverlay();
                });
            }
            catch (Exception ex)
            {
                Log.Exception($"Snip scroll stopped-hint failed: {ex.Message}", ex, typeof(ScrollCaptureController));
            }

            if (autoStopped)
            {
                Log.Info(
                    $"Snip scroll auto-stopped — {_tiles.Count} tile(s), pending stitch"
                    + (_heightCapReached ? $" (height cap {_maxHeightPx}px)" : string.Empty),
                    typeof(ScrollCaptureController));
            }

            SnipVerbose.Scroll(autoStopped
                ? $"[scroll] auto-stopped — {_tiles.Count} tile(s), pending stitch"
                : $"[scroll] manually stopped — {_tiles.Count} tile(s), pending stitch");
        }
        else
        {
            _stoppedPending = false;
            HideOverlay();
            if (autoStopped)
            {
                Log.Warn(
                    "Snip scroll auto-stopped with 0 tiles (capture/wheel failed)",
                    typeof(ScrollCaptureController));
            }
        }

        await NotifyCapturingStoppedOnceAsync().ConfigureAwait(false);
    }

    /// <summary>One-shot stitch of stored tiles for confirm/save. WriteableBitmap is created on the UI STA.</summary>
    public async Task<WriteableBitmap> StitchAsync()
    {
        _stitchingOverlayActive = true;
        _stoppedPending = false;
        WriteableBitmap resultBitmap = null;
        try
        {
            ShowOverlay();
            await ShowStitchingUiAsync().ConfigureAwait(true);

            if (_tiles.Count == 0)
            {
                SnipVerbose.Scroll("[scroll] stitch skipped — no tiles");
                return await _dispatcher.InvokeAsync(EmptyTile).Task.ConfigureAwait(true);
            }

            if (_tiles.Count == 1)
            {
                var only = _tiles[0];
                resultBitmap = await _dispatcher.InvokeAsync(only.ToWriteableBitmap).Task.ConfigureAwait(true);
            }
            else
            {
                var tilesCopy = OrderTilesForStitch(_tiles, _scrollUp);
                SnipVerbose.Scroll($"[scroll] stitching {tilesCopy.Length} raw tile(s) — pixel buffers on pool");
                using var stitchResult = await Task.Run(() => ScrollStitcher.Stitch(tilesCopy)).ConfigureAwait(true);
                resultBitmap = await _dispatcher.InvokeAsync(stitchResult.ToWriteableBitmap).Task.ConfigureAwait(true);
                SnipVerbose.Scroll($"[scroll] stitch complete — {tilesCopy.Length} tiles → h={resultBitmap.PixelHeight} truncated={stitchResult.Truncated}");
            }
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip scroll stitch failed: {ex.Message}", ex, typeof(ScrollCaptureController));
            if (_tiles.Count > 0)
            {
                var fallback = _tiles[0];
                resultBitmap = await _dispatcher.InvokeAsync(fallback.ToWriteableBitmap).Task.ConfigureAwait(true);
            }
            else
            {
                resultBitmap = await _dispatcher.InvokeAsync(EmptyTile).Task.ConfigureAwait(true);
            }
        }
        finally
        {
            _stitchingOverlayActive = false;
            HideOverlay();
        }

        DisposeTiles();
        return resultBitmap ?? await _dispatcher.InvokeAsync(EmptyTile).Task.ConfigureAwait(true);
    }

    public void Cancel() => _ = CancelAsync();

    public async Task CancelAsync()
    {
        if (!_active && _tiles.Count == 0 && !_stoppedPending)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _active = false;
            _stoppedPending = false;
            RestoreAfterScrollSession();
            HideOverlay();
            DisposeTiles();
            await InvokeCancelledAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip scroll cancel failed: {ex.Message}", ex, typeof(ScrollCaptureController));
            _active = false;
            _stoppedPending = false;
            try
            {
                RestoreAfterScrollSession();
                HideOverlay();
                DisposeTiles();
            }
            catch (Exception cleanupEx)
            {
                // Best-effort cleanup after a failed cancel.
                Log.Exception($"Snip scroll cancel cleanup failed: {cleanupEx.Message}", cleanupEx, typeof(ScrollCaptureController));
            }

            await InvokeCancelledAsync().ConfigureAwait(false);
        }
    }

    private async Task<ScrollTilePixels> CaptureOnceSafeAsync()
    {
        try
        {
            Interlocked.Exchange(ref _captureBusy, 1);

            // Capture + copy to managed BGRA on the thread pool — no WriteableBitmap yet.
            return await Task.Run(CaptureTilePixelsCore).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip scroll capture failed: {ex.Message}", ex, typeof(ScrollCaptureController));
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _captureBusy, 0);
        }
    }

    private void ProbeWindowCapturePreference()
    {
        _preferWindowCapture = false;
        _windowCaptureHwnd = IntPtr.Zero;
        _windowCaptureFailStreak = 0;
        try
        {
            int cx = _captureRect.X + (_captureRect.Width / 2);
            int cy = _captureRect.Y + (_captureRect.Height / 2);
            IntPtr hwnd = ScrollWindowCapture.ResolveUnderlayTopLevel(cx, cy, _overlayHwnd);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // Probe one frame; reject blank / undersized. Do not hide overlay for the probe.
            if (!ScrollWindowCapture.TryCaptureRect(
                    hwnd,
                    _captureRect.X,
                    _captureRect.Y,
                    _captureRect.Width,
                    _captureRect.Height,
                    out CapturedFrame probe)
                || probe == null)
            {
                return;
            }

            probe.Dispose();
            _windowCaptureHwnd = hwnd;
            _preferWindowCapture = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll window-capture probe failed: {ex.Message}", typeof(ScrollCaptureController));
            _preferWindowCapture = false;
            _windowCaptureHwnd = IntPtr.Zero;
        }
    }

    private ScrollTilePixels CaptureTilePixelsCore()
    {
        if (_preferWindowCapture && _windowCaptureHwnd != IntPtr.Zero)
        {
            if (ScrollWindowCapture.TryCaptureRect(
                    _windowCaptureHwnd,
                    _captureRect.X,
                    _captureRect.Y,
                    _captureRect.Width,
                    _captureRect.Height,
                    out CapturedFrame windowFrame)
                && windowFrame != null)
            {
                _windowCaptureFailStreak = 0;
                return windowFrame.ToOwnedPixels();
            }

            _windowCaptureFailStreak++;
            Log.Warn(
                $"Snip scroll window-capture tile failed (streak={_windowCaptureFailStreak})",
                typeof(ScrollCaptureController));
            if (_windowCaptureFailStreak >= 2)
            {
                _preferWindowCapture = false;

                // Switch to hide+BitBlt for the rest of the session.
                try
                {
                    BeginSessionOverlayHide();
                    Log.Info(
                        "Snip scroll: falling back to session SW_HIDE + BitBlt after window-capture failures",
                        typeof(ScrollCaptureController));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Snip scroll fallback hide failed: {ex.Message}", typeof(ScrollCaptureController));
                }
            }
        }

        using var frame = _capture.CaptureScreenRectPixels(
            _captureRect.X,
            _captureRect.Y,
            _captureRect.Width,
            _captureRect.Height);
        return frame.ToOwnedPixels();
    }

    private WriteableBitmap EmptyTile()
    {
        return new WriteableBitmap(
            Math.Max(1, _captureRect.Width),
            Math.Max(1, _captureRect.Height),
            96,
            96,
            PixelFormats.Bgra32,
            null);
    }

    private void DisposeTiles()
    {
        for (int i = 0; i < _tiles.Count; i++)
        {
            _tiles[i]?.Dispose();
        }

        _tiles.Clear();
        _stoppedPending = false;
    }

    private async Task ShowStitchingUiAsync()
    {
        await _dispatcher.InvokeAsync(() =>
        {
            EnsureOverlayAttached();
            _overlay.Visibility = Visibility.Visible;
            _overlay.ShowStitchingInProgress();
            LayoutOverlay();
        });
        await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(32).ConfigureAwait(true);
    }

    private void EnsureOverlayAttached()
    {
        if (_overlay.Parent is null)
        {
            _overlayHost.Children.Add(_overlay);
            Panel.SetZIndex(_overlay, 40);
        }
    }

    private void ShowOverlay()
    {
        try
        {
            _dispatcher.Invoke(() =>
            {
                EnsureOverlayAttached();
                _overlay.Visibility = Visibility.Visible;
                LayoutOverlay();
                if (!_stitchingOverlayActive && _active)
                {
                    _overlay.ShowManualCapturing(_tiles.Count);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip scroll show overlay failed: {ex.Message}", ex, typeof(ScrollCaptureController));
        }
    }

    private void HideOverlay()
    {
        try
        {
            _dispatcher.Invoke(() =>
            {
                _overlay.Visibility = Visibility.Collapsed;
                if (_overlay.Parent is Panel p)
                {
                    p.Children.Remove(_overlay);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip scroll hide overlay failed: {ex.Message}", ex, typeof(ScrollCaptureController));
        }
    }

    private void PostToOverlay(Action action)
    {
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (!_active && !_stitchingOverlayActive && !_stoppedPending)
                {
                    return;
                }

                action();
            }
            catch (Exception ex)
            {
                SnipVerbose.Scroll($"[scroll] overlay update skipped: {ex.Message}");
            }
        });
    }

    private async Task NotifyCapturingStoppedOnceAsync()
    {
        if (Interlocked.Exchange(ref _stopNotified, 1) != 0)
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(() => CapturingStopped?.Invoke(_tiles.Count));
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip scroll CapturingStopped callback failed: {ex.Message}", ex, typeof(ScrollCaptureController));
        }
    }

    private async Task InvokeCancelledAsync()
    {
        try
        {
            await _dispatcher.InvokeAsync(() => Cancelled?.Invoke());
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip scroll Cancelled callback failed: {ex.Message}", ex, typeof(ScrollCaptureController));
        }
    }

    private void LayoutOverlay()
    {
        try
        {
            double hostW = _overlayHost.ActualWidth;
            if (hostW <= 0 && _overlayHost is FrameworkElement fe)
            {
                hostW = fe.Width;
            }

            _overlay.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var sz = _overlay.DesiredSize;
            if (OverlayArea is Rect area && area.Width > 0)
            {
                Canvas.SetLeft(_overlay, Math.Max(area.X + 8, area.X + (area.Width / 2) - (sz.Width / 2)));
                Canvas.SetTop(_overlay, area.Y + 24);
                return;
            }

            Canvas.SetLeft(_overlay, Math.Max(8, (hostW / 2) - (sz.Width / 2)));
            Canvas.SetTop(_overlay, 24);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip scroll layout overlay failed: {ex.Message}", ex, typeof(ScrollCaptureController));
        }
    }
}
