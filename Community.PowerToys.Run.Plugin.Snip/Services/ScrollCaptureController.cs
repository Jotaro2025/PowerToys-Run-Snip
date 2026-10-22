// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Adapted from LdotJdot/TDS (MIT) ScrollCaptureController for WPF/Snip.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.UI;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Automatic scroll-capture loop: capture tile → SendWheel at selection center → delay → repeat.
/// Stitching happens once on confirm/save via <see cref="ScrollStitcher"/>.
/// </summary>
public sealed class ScrollCaptureController
{
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

    private readonly List<WriteableBitmap> _tiles = new();
    private CancellationTokenSource _cts;
    private bool _active;
    private int _captureBusy;
    private bool _stitchingOverlayActive;

    public event Action<int> CapturingStopped;

    public event Action Cancelled;

    public bool IsActive => _active;

    public int TileCount => _tiles.Count;

    public bool HasTiles => _tiles.Count > 0;

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
    /// Begin auto-scroll capture. Caller must hole-punch and hide adorners first.
    /// </summary>
    public void Begin()
    {
        if (_active)
        {
            return;
        }

        _active = true;
        Interlocked.Exchange(ref _captureBusy, 0);
        _tiles.Clear();

        ShowOverlay();
        PostToOverlay(() => _overlay.ShowManualCapturing(0));
        Debug.WriteLine("[scroll] auto-scroll started");

        _cts = new CancellationTokenSource();
        _ = AutoScrollLoopAsync(_cts.Token);
    }

    private async Task AutoScrollLoopAsync(CancellationToken ct)
    {
        var first = await CaptureOnceSafeAsync().ConfigureAwait(false);
        if (first != null && _active && !ct.IsCancellationRequested)
        {
            _tiles.Add(first);
        }

        while (!ct.IsCancellationRequested && _active && _tiles.Count <= _maxTiles)
        {
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

            _tiles.Add(bmp);
            PostToOverlay(() => _overlay.ShowManualCapturing(_tiles.Count));
            Debug.WriteLine($"[scroll] tile #{_tiles.Count} captured");
        }

        _active = false;
        HideOverlay();
        Debug.WriteLine($"[scroll] loop ended — {_tiles.Count} tile(s)");
        await InvokeCapturingStoppedAsync(_tiles.Count).ConfigureAwait(false);
    }

    private void SimulateWheelAtCenter()
    {
        int cx = _captureRect.X + (_captureRect.Width / 2);
        int cy = _captureRect.Y + (_captureRect.Height / 2);
        int delta = (_scrollUp ? 1 : -1) * Win32.WheelDelta * _stepsPerTick;

        if (!Win32CaptureService.TrySendWheelAt(cx, cy, delta, _overlayHwnd))
        {
            Debug.WriteLine($"[scroll] SimulateWheel failed at ({cx},{cy})");
        }
    }

    public async Task StopCapturingAsync()
    {
        if (!_active && _cts == null)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _active = false;
            HideOverlay();
            Debug.WriteLine($"[scroll] stopped — {_tiles.Count} raw tile(s) kept");
            await InvokeCapturingStoppedAsync(_tiles.Count).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[scroll] stop failed: {ex}");
            _active = false;
            try
            {
                HideOverlay();
            }
            catch
            {
                // ignore
            }

            await InvokeCapturingStoppedAsync(_tiles.Count).ConfigureAwait(false);
        }
    }

    /// <summary>One-shot stitch of stored tiles for confirm/save.</summary>
    public async Task<WriteableBitmap> StitchAsync()
    {
        _stitchingOverlayActive = true;
        WriteableBitmap resultBitmap = null;
        try
        {
            ShowOverlay();
            await ShowStitchingUiAsync().ConfigureAwait(true);

            if (_tiles.Count == 0)
            {
                Debug.WriteLine("[scroll] stitch skipped — no tiles");
                return EmptyTile();
            }

            if (_tiles.Count == 1)
            {
                resultBitmap = CloneTile(_tiles[0]);
            }
            else
            {
                var tilesCopy = _tiles.ToArray();
                Debug.WriteLine($"[scroll] stitching {tilesCopy.Length} raw tile(s) — one-time merge");
                var stitchResult = await Task.Run(() => ScrollStitcher.Stitch(tilesCopy)).ConfigureAwait(true);
                resultBitmap = stitchResult.Bitmap;
                Debug.WriteLine($"[scroll] stitch complete — {tilesCopy.Length} tiles → h={resultBitmap.PixelHeight}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[scroll] stitch failed: {ex}");
            resultBitmap = _tiles.Count > 0 ? CloneTile(_tiles[0]) : EmptyTile();
        }
        finally
        {
            _stitchingOverlayActive = false;
            HideOverlay();
        }

        DisposeTiles();
        return resultBitmap ?? EmptyTile();
    }

    public void Cancel() => _ = CancelAsync();

    public async Task CancelAsync()
    {
        if (!_active && _tiles.Count == 0)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _active = false;
            HideOverlay();
            DisposeTiles();
            await InvokeCancelledAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[scroll] cancel failed: {ex}");
            _active = false;
            try
            {
                HideOverlay();
                DisposeTiles();
            }
            catch
            {
                // ignore
            }

            await InvokeCancelledAsync().ConfigureAwait(false);
        }
    }

    private async Task<WriteableBitmap> CaptureOnceSafeAsync()
    {
        CapturedFrame frame = null;
        try
        {
            Interlocked.Exchange(ref _captureBusy, 1);
            frame = await Task.Run(
                () => _capture.CaptureScreenRectPixels(
                    _captureRect.X,
                    _captureRect.Y,
                    _captureRect.Width,
                    _captureRect.Height)).ConfigureAwait(false);

            // WriteableBitmap must be created on the UI STA.
            // ToWriteableBitmap disposes the CapturedFrame after the Span copy.
            var wb = await _dispatcher.InvokeAsync(frame.ToWriteableBitmap).Task.ConfigureAwait(false);
            frame = null;
            return wb;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[scroll] capture failed: {ex.Message}");
            return null;
        }
        finally
        {
            frame?.Dispose();
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

    private static WriteableBitmap CloneTile(WriteableBitmap src)
    {
        int w = src.PixelWidth;
        int h = src.PixelHeight;
        var clone = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        src.Lock();
        clone.Lock();
        try
        {
            unsafe
            {
                int rowBytes = w * 4;
                byte* s = (byte*)src.BackBuffer;
                byte* d = (byte*)clone.BackBuffer;
                for (int y = 0; y < h; y++)
                {
                    Buffer.MemoryCopy(
                        s + ((long)y * src.BackBufferStride),
                        d + ((long)y * clone.BackBufferStride),
                        rowBytes,
                        rowBytes);
                }
            }

            clone.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            clone.Unlock();
            src.Unlock();
        }

        return clone;
    }

    private void DisposeTiles()
    {
        _tiles.Clear();
    }

    private async Task ShowStitchingUiAsync()
    {
        await _dispatcher.InvokeAsync(() =>
        {
            _overlay.ShowStitchingInProgress();
            LayoutOverlay();
        });
        await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(32).ConfigureAwait(true);
    }

    private void ShowOverlay()
    {
        try
        {
            _dispatcher.Invoke(() =>
            {
                if (_overlay.Parent is null)
                {
                    _overlayHost.Children.Add(_overlay);
                    Panel.SetZIndex(_overlay, 40);
                }

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
            Debug.WriteLine($"[scroll] ShowOverlay failed: {ex.Message}");
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
            Debug.WriteLine($"[scroll] HideOverlay failed: {ex.Message}");
        }
    }

    private void PostToOverlay(Action action)
    {
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (!_active && !_stitchingOverlayActive)
                {
                    return;
                }

                action();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[scroll] overlay update skipped: {ex.Message}");
            }
        });
    }

    private async Task InvokeCapturingStoppedAsync(int count)
    {
        try
        {
            await _dispatcher.InvokeAsync(() => CapturingStopped?.Invoke(count));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[scroll] CapturingStopped callback failed: {ex.Message}");
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
            Debug.WriteLine($"[scroll] Cancelled callback failed: {ex.Message}");
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
            Canvas.SetLeft(_overlay, Math.Max(8, (hostW / 2) - (sz.Width / 2)));
            Canvas.SetTop(_overlay, 24);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[scroll] LayoutOverlay failed: {ex.Message}");
        }
    }
}
