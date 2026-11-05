// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

public sealed partial class SnipWindow
{
    private ScrollCaptureController _scrollController;
    private ScrollLivePreview _scrollLivePreview;
    private bool _scrollTransition;
    private bool _scrollStitching;

    private void OnScrollCaptureToggled(object sender, EventArgs e) => ToggleScrollCapture(scrollUp: false);

    private void OnScrollUpCaptureToggled(object sender, EventArgs e) => ToggleScrollCapture(scrollUp: true);

    private async void ToggleScrollCapture(bool scrollUp)
    {
        if (_disposed || _scrollTransition || _scrollStitching)
        {
            return;
        }

        _scrollTransition = true;
        try
        {
            if (_scrollController is { IsActive: true })
            {
                await StopScrollCaptureAsync().ConfigureAwait(true);
            }
            else if (_hasSelection)
            {
                if (_scrollController != null)
                {
                    await DiscardScrollTilesAsync().ConfigureAwait(true);
                }

                if (!_disposed)
                {
                    StartScrollCapture(scrollUp);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow scroll toggle failed: {ex.Message}", ex, GetType());
            EnsureScrollUiRecovered();
        }
        finally
        {
            _scrollTransition = false;
        }
    }

    private void StartScrollCapture(bool scrollUp = false)
    {
        if (!_hasSelection || _gifSession != null || _gifSaving)
        {
            return;
        }

        var physicalBounds = new Int32Rect(
            _physicalBounds.X,
            _physicalBounds.Y,
            Math.Max(1, _physicalBounds.Width),
            Math.Max(1, _physicalBounds.Height));
        var physicalRect = DpiCoordinateMapping.DipSelectionToPhysical(
            _selection,
            _logicalSize,
            physicalBounds);

        _scrollController = new ScrollCaptureController(
            _captureService,
            _root,
            physicalRect,
            TryGetHwnd(),
            scrollIntervalMs: Math.Clamp(_settings.ScrollIntervalMs, 50, 1000),
            scrollUp: scrollUp,
            maxTiles: Math.Clamp(_settings.ScrollMaxTiles, 10, 500));
        if (_spansMonitors)
        {
            _scrollController.OverlayArea = MonitorAreaFor(_selection);
        }

        _scrollController.CapturingStopped += OnScrollCapturingStopped;
        _scrollController.Cancelled += OnScrollCaptureCancelled;
        _toolbar.SetScrollCaptureActive(true, scrollUp);

        // Probe before blanking UI: #100 window-capture keeps the overlay visible, so hiding
        // the frozen preview + punching a hole leaves a persistent blank selection (白屏)
        // while PrintWindow still stitches. Keep frozen preview on that path.
        bool keepFrozenPreview = ScrollCaptureController.ShouldKeepFrozenScrollPreview(
            _scrollController.ProbeWindowCapture());

        // Keep XOR dim around the selection (not a transparent root / full-client blank).
        _dimPath.Fill = _selectionDimBrush;
        var outer = new RectangleGeometry(new Rect(0, 0, _logicalSize.Width, _logicalSize.Height));
        var inner = new RectangleGeometry(_selection);
        _dimPath.Data = new CombinedGeometry(GeometryCombineMode.Xor, outer, inner);
        _dimPath.Visibility = Visibility.Visible;
        _dimPath.IsHitTestVisible = false;
        _inputCatcher.IsHitTestVisible = false;
        _annoCanvas.IsHitTestVisible = false;
        _annoCanvas.Visibility = Visibility.Collapsed;
        _annoCanvas.Clip = null;
        _toolbar.IsHitTestVisible = true;
        _adorner.IsVisible = false;
        _adorner.UpdateLayout();

        if (keepFrozenPreview)
        {
            // Window-capture path: overlay stays visible — show live-updated window frames
            // inside the selection without punching a hole or mutating _source.
            var previewRect = new Int32Rect(
                Math.Max(0, physicalRect.X - _physicalBounds.X),
                Math.Max(0, physicalRect.Y - _physicalBounds.Y),
                physicalRect.Width,
                physicalRect.Height);
            _scrollLivePreview = ScrollLivePreview.Create(_source, previewRect);
            if (_scrollLivePreview?.Bitmap != null)
            {
                _screenshotImage.Source = _scrollLivePreview.Bitmap;
            }

            _scrollController.LatestTileCaptured += OnScrollLatestTileCaptured;
            _screenshotImage.Opacity = 1;
            _screenshotImage.Visibility = Visibility.Visible;
            ClearScrollCaptureHole();
            UpdateLayout();
            Log.Info(
                "Snip scroll UI: keep live selection preview (window-capture; avoid white hole)",
                GetType());
        }
        else
        {
            // SW_HIDE path (#89/#90): hide frozen screenshot before hole so DWM does not
            // flash white over the still-painted bitmap (#87); overlay will hide shortly.
            _screenshotImage.Opacity = 0;
            _screenshotImage.Visibility = Visibility.Collapsed;
            UpdateLayout();
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            if (!ApplyScrollCaptureHole())
            {
                _screenshotImage.Opacity = 1;
                _screenshotImage.Visibility = Visibility.Visible;
                Log.Warn(
                    "Snip scroll: selection hole not applied (full-client or invalid); showing screenshot again; wheel uses Z-order underlay",
                    GetType());
            }
        }

        _scrollController.Begin();

        if (!keepFrozenPreview)
        {
            // ShowOverlay may trigger another layout; re-apply hole so hit-test region stays punched.
            _ = ApplyScrollCaptureHole();
        }
    }

    private bool ApplyScrollCaptureHole()
    {
        var hwnd = TryGetHwnd();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        int clientW = Math.Max(1, (int)Math.Round(_logicalSize.Width * _dpiScale));
        int clientH = Math.Max(1, (int)Math.Round(_logicalSize.Height * _dpiScale));
        if (Win32WindowRegion.TryGetClientSize(hwnd, out int cw, out int ch))
        {
            clientW = cw;
            clientH = ch;
        }

        var hole = DpiCoordinateMapping.DipSelectionToClientHole(
            _selection,
            _logicalSize,
            clientW,
            clientH);

        return Win32WindowRegion.ApplySelectionHole(
            hwnd, clientW, clientH, hole.X, hole.Y, hole.Width, hole.Height);
    }

    private void ClearScrollCaptureHole()
    {
        var hwnd = TryGetHwnd();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        Win32WindowRegion.Clear(hwnd);
    }

    private async Task StopScrollCaptureAsync()
    {
        try
        {
            var ctl = _scrollController;
            if (ctl == null || !ctl.IsActive)
            {
                return;
            }

            await ctl.StopCapturingAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow StopScrollCaptureAsync: {ex.Message}", ex, GetType());
            EnsureScrollUiRecovered();
        }
    }

    private async Task FinishScrollCaptureAndConfirmAsync()
    {
        if (_disposed || _scrollStitching)
        {
            return;
        }

        _scrollStitching = true;
        try
        {
            var ctl = _scrollController;
            if (ctl == null || (!ctl.IsActive && !ctl.HasTiles))
            {
                return;
            }

            if (ctl.IsActive)
            {
                await ctl.StopCapturingAsync().ConfigureAwait(true);
            }

            if (ctl.TileCount < 1)
            {
                return;
            }

            var stitched = await ctl.StitchAsync().ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(_scrollController, ctl) || stitched == null || stitched.PixelWidth < 1 || stitched.PixelHeight < 1)
            {
                return;
            }

            ApplyStitchedScrollResult(stitched);
            DetachScrollController(ctl);
            _scrollController = null;
            await DoConfirmAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow FinishScrollCaptureAndConfirmAsync: {ex.Message}", ex, GetType());
            EnsureScrollUiRecovered();
        }
        finally
        {
            _scrollStitching = false;
        }
    }

    private async Task<System.Windows.Media.Imaging.WriteableBitmap> StitchScrollTilesForExportAsync()
    {
        if (_disposed || _scrollStitching)
        {
            return null;
        }

        _scrollStitching = true;
        try
        {
            var ctl = _scrollController;
            if (ctl == null || (!ctl.IsActive && !ctl.HasTiles))
            {
                return null;
            }

            if (ctl.IsActive)
            {
                await ctl.StopCapturingAsync().ConfigureAwait(true);
            }

            if (ctl.TileCount < 1)
            {
                return null;
            }

            var stitched = await ctl.StitchAsync().ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(_scrollController, ctl) || stitched == null || stitched.PixelWidth < 1 || stitched.PixelHeight < 1)
            {
                return null;
            }

            ApplyStitchedScrollResult(stitched);
            DetachScrollController(ctl);
            _scrollController = null;
            return stitched;
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow StitchScrollTilesForExportAsync: {ex.Message}", ex, GetType());
            EnsureScrollUiRecovered();
            return null;
        }
        finally
        {
            _scrollStitching = false;
        }
    }

    private void OnScrollCapturingStopped(int tileCount)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _toolbar.SetScrollCaptureActive(false);
            if (tileCount > 0)
            {
                // Keep selection hole closed (frozen overlay) but leave the stopped hint
                // card visible until stitch / discard. Esc now discards frames only.
                ClearScrollCaptureHole();
                if (_source != null)
                {
                    _screenshotImage.Source = (System.Windows.Media.Imaging.BitmapSource)_scrollLivePreview?.Bitmap ?? BitmapPreview.ForOverlay(_source);
                    _annoCanvas.SourceBitmap = _source;
                    _screenshotImage.Opacity = 1;
                    _screenshotImage.Visibility = Visibility.Visible;
                }

                _inputCatcher.IsHitTestVisible = true;
                _annoCanvas.IsHitTestVisible = true;
                _toolbar.IsHitTestVisible = true;
                UpdateSelectionVisuals();
                _toolbar.SetScrollTilesPending(true);
            }
            else
            {
                RestorePassthroughUi();
                _toolbar.SetScrollTilesPending(false);
                var controller = _scrollController;
                DetachScrollController(controller);
                _scrollController = null;
                if (controller != null)
                {
                    _ = controller.CancelAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow OnScrollCapturingStopped: {ex.Message}", ex, GetType());
            EnsureScrollUiRecovered();
        }
    }

    /// <summary>
    /// Discard captured scroll tiles without closing the snip session (Esc after stop).
    /// </summary>
    private async Task DiscardScrollTilesAsync()
    {
        try
        {
            var ctl = _scrollController;
            if (ctl == null)
            {
                return;
            }

            DetachScrollController(ctl);
            await ctl.CancelAsync().ConfigureAwait(true);
            if (_disposed || !ReferenceEquals(_scrollController, ctl))
            {
                return;
            }

            _toolbar.SetScrollTilesPending(false);
            _toolbar.SetScrollCaptureActive(false);
            _scrollController = null;
            RestorePassthroughUi();
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow DiscardScrollTilesAsync: {ex.Message}", ex, GetType());
            EnsureScrollUiRecovered();
            _scrollController = null;
        }
    }

    private void ApplyStitchedScrollResult(System.Windows.Media.Imaging.WriteableBitmap stitched)
    {
        _source = stitched;

        double newW = _source.PixelWidth / _dpiScale;
        double newH = _source.PixelHeight / _dpiScale;
        _selection = new Rect(0, 0, newW, newH);
        _hasSelection = true;

        _screenshotImage.Source = stitched;
        _annoCanvas.SourceBitmap = stitched;
        _annoCanvas.SourceDpiScale = _dpiScale;
        _annoCanvas.SourceOffset = new Point(0, 0);
        _toolbar.SetScrollTilesPending(false);
        RestorePassthroughUi();
    }

    private void OnScrollCaptureCancelled()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            RestorePassthroughUi();
            _toolbar.SetScrollCaptureActive(false);
            _toolbar.SetScrollTilesPending(false);
            DetachScrollController(_scrollController);
            _scrollController = null;
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow OnScrollCaptureCancelled: {ex.Message}", ex, GetType());
            EnsureScrollUiRecovered();
            _scrollController = null;
        }
    }

    private void OnScrollLatestTileCaptured(ScrollTilePixels tile)
    {
        if (!_disposed)
        {
            _scrollLivePreview?.BlitTile(tile);
        }
    }

    private void DetachScrollController(ScrollCaptureController controller)
    {
        if (controller == null)
        {
            return;
        }

        controller.CapturingStopped -= OnScrollCapturingStopped;
        controller.Cancelled -= OnScrollCaptureCancelled;
        controller.LatestTileCaptured -= OnScrollLatestTileCaptured;
    }

    private void RestorePassthroughUi()
    {
        _scrollLivePreview?.Reset();
        _scrollLivePreview = null;
        ClearScrollCaptureHole();
        _dimPath.IsHitTestVisible = false;
        if (_source != null)
        {
            _screenshotImage.Source = BitmapPreview.ForOverlay(_source);
            _annoCanvas.SourceBitmap = _source;
            _screenshotImage.Opacity = 1;
            _screenshotImage.Visibility = Visibility.Visible;
        }

        _inputCatcher.IsHitTestVisible = true;
        _annoCanvas.IsHitTestVisible = true;
        _toolbar.IsHitTestVisible = true;
        UpdateSelectionVisuals();
    }

    private void EnsureScrollUiRecovered()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _toolbar.SetScrollCaptureActive(false);
            _toolbar.SetScrollTilesPending(false);
            ClearScrollCaptureHole();
            RestorePassthroughUi();
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow EnsureScrollUiRecovered: {ex.Message}", ex, GetType());
        }
    }

    private IntPtr TryGetHwnd()
    {
        try
        {
            return new WindowInteropHelper(this).Handle;
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow TryGetHwnd failed: {ex.Message}", ex, GetType());
            return IntPtr.Zero;
        }
    }
}
