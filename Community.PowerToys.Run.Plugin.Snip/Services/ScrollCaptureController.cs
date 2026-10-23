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
    internal const int ConsecutiveDuplicateStopThreshold = 2;

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
        _ = AutoScrollLoopAsync(_cts.Token);
    }

    private async Task AutoScrollLoopAsync(CancellationToken ct)
    {
        int consecutiveDups = 0;
        int projectedHeight = 0;
        int maxHeight = int.MaxValue;
        int scrollHint = 0;
        var first = await CaptureOnceSafeAsync().ConfigureAwait(false);
        if (first != null && _active && !ct.IsCancellationRequested)
        {
            _tiles.Add(first);
            projectedHeight = first.Height;
            maxHeight = ScrollStitcher.ResolveMaxStitchedHeight(first.Width);
        }

        // Mouse guard arms once the cursor has been inside the selection, so starting
        // from the toolbar (outside the selection) does not pause immediately.
        bool guardArmed = false;
        while (!ct.IsCancellationRequested && _active && _tiles.Count < _maxTiles)
        {
            bool inside = IsCursorInsideCapture();
            guardArmed |= inside;
            if (guardArmed && !inside)
            {
                if (!_paused)
                {
                    _paused = true;
                    PostToOverlay(() => _overlay.ShowPaused());
                    SnipVerbose.Scroll("[scroll] paused — cursor left selection");
                }

                try
                {
                    await Task.Delay(_scrollIntervalMs, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                continue;
            }

            if (_paused)
            {
                _paused = false;
                SnipVerbose.Scroll("[scroll] resumed — cursor back in selection");
            }

            PostToOverlay(() => _overlay.ShowCapturing(_tiles.Count + 1));

            SimulateWheelAtCenter();

            try
            {
                await Task.Delay(_scrollIntervalMs, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            var bmp = await CaptureOnceSafeAsync().ConfigureAwait(false);
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
                consecutiveDups++;
                bmp.Dispose();
                SnipVerbose.Scroll($"[scroll] duplicate tile (streak={consecutiveDups}) — not kept");
                if (consecutiveDups >= ConsecutiveDuplicateStopThreshold)
                {
                    SnipVerbose.Scroll("[scroll] auto-stop — page no longer scrolling");
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
            SnipVerbose.Scroll($"[scroll] tile #{_tiles.Count} captured (projected h={projectedHeight}/{maxHeight})");

            if (projectedHeight >= maxHeight)
            {
                _heightCapReached = true;
                _maxHeightPx = maxHeight;
                SnipVerbose.Scroll("[scroll] auto-stop — stitched height cap reached");
                break;
            }
        }

        _active = false;
        await FinishCapturingUiAsync(autoStopped: !ct.IsCancellationRequested).ConfigureAwait(false);
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

    private void SimulateWheelAtCenter()
    {
        int cx = _captureRect.X + (_captureRect.Width / 2);
        int cy = _captureRect.Y + (_captureRect.Height / 2);
        int delta = (_scrollUp ? 1 : -1) * Win32.WheelDelta * _stepsPerTick;

        if (!Win32CaptureService.TrySendWheelAt(cx, cy, delta, _overlayHwnd))
        {
            SnipVerbose.Scroll($"[scroll] SimulateWheel failed at ({cx},{cy})");
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
            SnipVerbose.Scroll($"[scroll] stopped — {_tiles.Count} raw tile(s) kept");
            await FinishCapturingUiAsync(autoStopped: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip scroll stop failed: {ex.Message}", ex, typeof(ScrollCaptureController));
            _active = false;
            await FinishCapturingUiAsync(autoStopped: false).ConfigureAwait(false);
        }
    }

    /// <summary>Show stopped hint (keep overlay) and raise CapturingStopped once.</summary>
    private async Task FinishCapturingUiAsync(bool autoStopped)
    {
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

            SnipVerbose.Scroll(autoStopped
                ? $"[scroll] auto-stopped — {_tiles.Count} tile(s), pending stitch"
                : $"[scroll] manually stopped — {_tiles.Count} tile(s), pending stitch");
        }
        else
        {
            _stoppedPending = false;
            HideOverlay();
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
            return await Task.Run(() =>
            {
                using var frame = _capture.CaptureScreenRectPixels(
                    _captureRect.X,
                    _captureRect.Y,
                    _captureRect.Width,
                    _captureRect.Height);
                return frame.ToOwnedPixels();
            }).ConfigureAwait(false);
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
