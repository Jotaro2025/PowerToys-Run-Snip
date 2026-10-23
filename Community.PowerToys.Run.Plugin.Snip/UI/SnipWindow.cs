// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Fullscreen borderless window that hosts the screenshot capture, selection,
/// and annotation UI. Confirm / copy / save / cancel end the session; OCR /
/// image-translate / pin keep the overlay (hide while a result window is open,
/// then restore) so the user can annotate again on the same capture.
/// </summary>
public sealed class SnipWindow : Window
{
    private static readonly Color ChromeColor = Color.FromRgb(80, 220, 100);

    /// <summary>Subtle hint that screenshot mode is active (no selection yet).</summary>
    private readonly SolidColorBrush _idleDimBrush = new SolidColorBrush(Color.FromArgb(88, 0, 0, 0));

    /// <summary>Stronger dim outside the selected region.</summary>
    private readonly SolidColorBrush _selectionDimBrush = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0));

    private readonly Int32Rect _physicalBounds;

    /// <summary>Physical bounds of each real monitor when the overlay spans the virtual desktop; else empty.</summary>
    private readonly Int32Rect[] _monitorBounds;

    /// <summary>True when the overlay covers several monitors (virtual desktop capture).</summary>
    private readonly bool _spansMonitors;
    private readonly Canvas _root;

    private readonly SnipBackground _screenshotImage;
    private readonly System.Windows.Shapes.Path _dimPath;
    private readonly Border _inputCatcher;
    private readonly AnnotationCanvas _annoCanvas;
    private readonly SelectionAdorner _adorner;
    private readonly SnipToolbar _toolbar;
    private readonly SnipSettings _settings;
    private readonly ICaptureService _captureService = CaptureFactory.Create();

    private System.Windows.Media.Imaging.WriteableBitmap _source;
    private Size _logicalSize;
    private double _dpiScale;
    private Rect _selection;
    private bool _hasSelection;
    private bool _dragCreating;
    private Point _dragStartPoint;
    private Rect _dragStartSelection;
    private bool _dragMovingHandle;
    private bool _disposed;
    private bool _exportBusy;

    /// <summary>Modeless OCR/translate window opened without ending the snip session.</summary>
    private Window _sessionChild;

    /// <summary>True when the overlay was <see cref="Window.Hide"/> for a session child.</summary>
    private bool _hiddenForSessionChild;

    private ScrollCaptureController _scrollController;

    public SnipWindow(
        System.Windows.Media.Imaging.WriteableBitmap source,
        ScreenInfo screen,
        EditRequest request,
        SnipSettings settings = null,
        System.Collections.Generic.IReadOnlyList<ScreenInfo> monitors = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(request);

        _settings = settings ?? new SnipSettings();

        _source = source;
        _physicalBounds = new Int32Rect(
            (int)screen.Bounds.X,
            (int)screen.Bounds.Y,
            (int)screen.Bounds.Width,
            (int)screen.Bounds.Height);
        _dpiScale = screen.DpiScale > 0 ? screen.DpiScale : 1.0;
        _logicalSize = new Size(_physicalBounds.Width / _dpiScale, _physicalBounds.Height / _dpiScale);
        _spansMonitors = screen.DeviceName == Win32CaptureService.VirtualDesktopName && monitors is { Count: > 1 };
        _monitorBounds = _spansMonitors
            ? monitors.Select(m => new Int32Rect((int)m.Bounds.X, (int)m.Bounds.Y, (int)m.Bounds.Width, (int)m.Bounds.Height)).ToArray()
            : [];

        // Window configuration: borderless, topmost, full coverage.
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _physicalBounds.X / _dpiScale;
        Top = _physicalBounds.Y / _dpiScale;
        Width = _logicalSize.Width;
        Height = _logicalSize.Height;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        Title = Properties.Resources.plugin_name;

        // Overlay may show a downsampled preview for large (near-fullscreen)
        // captures so WPF paints less; _source stays full-res for mosaic /
        // BuildResultBitmap / OCR / translate / save / copy.
        _screenshotImage = new SnipBackground
        {
            Source = BitmapPreview.ForOverlay(source),
            Width = _logicalSize.Width,
            Height = _logicalSize.Height,
            IsHitTestVisible = false,
        };

        _dimPath = new System.Windows.Shapes.Path
        {
            Fill = _idleDimBrush,
            IsHitTestVisible = false,
        };

        _inputCatcher = new Border
        {
            Background = Brushes.Transparent,
            IsHitTestVisible = true,
            Width = _logicalSize.Width,
            Height = _logicalSize.Height,
        };

        var initialStroke = request.DefaultStroke
            ?? SnipSettings.ParseColorHexOrDefault(_settings.LastStrokeColorHex, Color.FromRgb(238, 32, 77));
        double initialWidth = request.DefaultStrokeWidth > 0 ? request.DefaultStrokeWidth : (_settings.LastStrokeWidth > 0 ? _settings.LastStrokeWidth : 3.0);

        _annoCanvas = new AnnotationCanvas
        {
            SourceBitmap = source,
            SourceOffset = new Point(0, 0),
            SourceDpiScale = _dpiScale,
            ActiveTool = request.InitialTool,
            CurrentStroke = initialStroke,
            CurrentStrokeWidth = initialWidth,
            Width = _logicalSize.Width,
            Height = _logicalSize.Height,
            ClipToBounds = true,
            IsHitTestVisible = true,
            Focusable = true,
            Visibility = Visibility.Collapsed,
        };
        _annoCanvas.HistoryChanged += (sender, e) =>
            _toolbar.NotifyHistory(_annoCanvas.UndoDepth, _annoCanvas.RedoDepth);
        _toolbar = new SnipToolbar(request) { Visibility = Visibility.Collapsed };
        _toolbar.ToolChanged += OnToolbarToolChanged;
        _toolbar.MosaicEffectChanged += (sender, effect) => _annoCanvas.CurrentMosaicEffect = effect;
        _toolbar.ColorChanged += (sender, c) =>
        {
            _annoCanvas.CurrentStroke = c;
            PersistAnnotationBrush(c, _annoCanvas.CurrentStrokeWidth);
        };
        _toolbar.StrokeWidthChanged += (sender, w) =>
        {
            _annoCanvas.CurrentStrokeWidth = w;
            PersistAnnotationBrush(_annoCanvas.CurrentStroke, w);
        };
        _toolbar.UndoClicked += (sender, e) => _annoCanvas.Undo();
        _toolbar.RedoClicked += (sender, e) => _annoCanvas.Redo();
        _toolbar.SaveClicked += async (sender, e) => await DoSaveAsync();
        _toolbar.ExtractTextClicked += async (sender, e) => await DoExtractTextAsync();
        _toolbar.TranslateImageClicked += async (sender, e) => await DoTranslateImageAsync();
        _toolbar.PinClicked += async (sender, e) => await DoPinAsync();
        _toolbar.CancelClicked += (sender, e) =>
        {
            // After scroll stop: Cancel discards frames only (same as Esc), not the whole snip.
            if (_scrollController is { HasTiles: true, IsActive: false })
            {
                _ = DiscardScrollTilesAsync();
                return;
            }

            Cancel();
        };
        _toolbar.ConfirmClicked += async (sender, e) => await DoConfirmAsync();
        _toolbar.ScrollCaptureClicked += OnScrollCaptureToggled;
        _toolbar.ScrollUpCaptureClicked += OnScrollUpCaptureToggled;

        _root = new Canvas { ClipToBounds = true, Background = Brushes.Transparent };
        _root.Children.Add(_screenshotImage);
        Canvas.SetLeft(_screenshotImage, 0);
        Canvas.SetTop(_screenshotImage, 0);

        _root.Children.Add(_dimPath);

        // _inputCatcher sits below the annotation/adorner layers so clicks
        // inside the selection reach AnnotationCanvas, while clicks outside
        // still start a new selection drag.
        _root.Children.Add(_inputCatcher);
        Canvas.SetLeft(_inputCatcher, 0);
        Canvas.SetTop(_inputCatcher, 0);
        Panel.SetZIndex(_inputCatcher, 1);

        _root.Children.Add(_annoCanvas);
        Canvas.SetLeft(_annoCanvas, 0);
        Canvas.SetTop(_annoCanvas, 0);
        Panel.SetZIndex(_annoCanvas, 10);

        // Border + 8 resize handles are added directly to root so they sit
        // above AnnotationCanvas but do not block pointer events elsewhere.
        _adorner = new SelectionAdorner(_root);
        _adorner.HandleDrag += OnHandleDrag;

        _root.Children.Add(_toolbar);
        Panel.SetZIndex(_toolbar, 30);

        Content = _root;

        _inputCatcher.MouseLeftButtonDown += OnRootPointerPressed;
        _inputCatcher.MouseMove += OnRootPointerMoved;
        _inputCatcher.MouseLeftButtonUp += OnRootPointerReleased;

        // Also listen on root so drags that start on the catcher keep
        // receiving move/release even when the pointer leaves its bounds.
        _root.MouseMove += OnRootPointerMoved;
        _root.MouseLeftButtonUp += OnRootPointerReleased;

        // Tunnel so Esc closes immediately even when a child (e.g. inline TextBox) has focus.
        PreviewKeyDown += OnKeyDown;

        // Right-click cancels the whole session (same as Esc / Cancel toolbar),
        // including mid-drag selection. Toolbar buttons only use left click.
        PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;

        // Pre-fill selection for full-screen / active-window; otherwise idle dim until drag.
        if (request.InitialSelection is { } sel && sel.Width >= 5 && sel.Height >= 5)
        {
            _selection = sel;
            _hasSelection = true;
        }

        UpdateSelectionVisuals();
    }

    public EditResult Result { get; private set; }

    private bool ShouldShowSelectionVignette()
        => _hasSelection
           || (_dragCreating && _selection.Width >= 5 && _selection.Height >= 5);

    // -----------------------------------------------------------------------------------
    //  Window lifecycle
    // -----------------------------------------------------------------------------------
    private void OnSourceInitialized(object sender, EventArgs e)
    {
        ApplyNativePhysicalBounds();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyNativePhysicalBounds();
        if (_spansMonitors)
        {
            // The virtual-desktop entry starts with the primary monitor's DPI; the window really
            // renders at the DPI of the monitor Windows assigned it to. Use that one scale for
            // every DIP <-> physical conversion (per-monitor-v2 windows are not bitmap-scaled).
            ApplyWindowDpiScale(VisualTreeHelper.GetDpi(this).DpiScaleX);
            ApplyNativePhysicalBounds();
        }

        Activate();
        _annoCanvas.Focus();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _ = Win32.SetForegroundWindow(hwnd);
        }
    }

    private void OnClosed(object sender, EventArgs e)
    {
        DismissSessionChild(restoreOverlay: false);
        CleanupResources();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (!_spansMonitors)
        {
            return;
        }

        // WPF resizes the window to the suggested rect; keep our physical virtual-desktop rect
        // and switch every DIP layout to the new window DPI instead.
        ApplyWindowDpiScale(newDpi.DpiScaleX);
        Dispatcher.BeginInvoke(new Action(ApplyNativePhysicalBounds), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Re-derives the DIP layout from the fixed physical bounds for a new window DPI scale.
    /// Only used when the overlay spans monitors; single-monitor sessions keep the monitor DPI.
    /// </summary>
    private void ApplyWindowDpiScale(double scale)
    {
        if (scale <= 0 || Math.Abs(scale - _dpiScale) < 0.0001)
        {
            return;
        }

        double ratio = _dpiScale / scale;
        _dpiScale = scale;
        _logicalSize = new Size(_physicalBounds.Width / scale, _physicalBounds.Height / scale);
        Width = _logicalSize.Width;
        Height = _logicalSize.Height;
        _screenshotImage.Width = _logicalSize.Width;
        _screenshotImage.Height = _logicalSize.Height;
        _inputCatcher.Width = _logicalSize.Width;
        _inputCatcher.Height = _logicalSize.Height;
        _annoCanvas.Width = _logicalSize.Width;
        _annoCanvas.Height = _logicalSize.Height;
        _annoCanvas.SourceDpiScale = scale;
        if (_hasSelection || _dragCreating)
        {
            _selection = new Rect(_selection.X * ratio, _selection.Y * ratio, _selection.Width * ratio, _selection.Height * ratio);
        }

        UpdateSelectionVisuals();
    }

    /// <summary>
    /// DIP rect (overlay coordinates) of the monitor that holds most of <paramref name="selection"/>;
    /// the whole overlay when it covers a single monitor.
    /// </summary>
    private Rect MonitorAreaFor(Rect selection)
    {
        var whole = new Rect(0, 0, _logicalSize.Width, _logicalSize.Height);
        if (_monitorBounds.Length == 0)
        {
            return whole;
        }

        var center = new Point(selection.X + (selection.Width / 2), selection.Y + (selection.Height / 2));
        Rect best = Rect.Empty;
        double bestScore = double.NegativeInfinity;
        foreach (var m in _monitorBounds)
        {
            var dip = new Rect(
                (m.X - _physicalBounds.X) / _dpiScale,
                (m.Y - _physicalBounds.Y) / _dpiScale,
                m.Width / _dpiScale,
                m.Height / _dpiScale);
            if (dip.Contains(center))
            {
                return dip;
            }

            var overlap = Rect.Intersect(dip, selection);
            double score = overlap.IsEmpty ? -DistanceSquared(dip, center) : overlap.Width * overlap.Height;
            if (score > bestScore)
            {
                bestScore = score;
                best = dip;
            }
        }

        return best.IsEmpty ? whole : best;
    }

    private static double DistanceSquared(Rect r, Point p)
    {
        double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
        double dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        return (dx * dx) + (dy * dy);
    }

    /// <summary>
    /// Force the native window to cover the monitor in physical pixels; WPF
    /// layout rounding can otherwise leave a 1px gap on scaled displays.
    /// </summary>
    private void ApplyNativePhysicalBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        Win32.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            _physicalBounds.X,
            _physicalBounds.Y,
            _physicalBounds.Width,
            _physicalBounds.Height,
            Win32.SwpNoZOrder | Win32.SwpShowWindow);
    }

    // -----------------------------------------------------------------------------------
    //  Selection drag (creating a new region)
    // -----------------------------------------------------------------------------------
    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Same as Esc / Cancel. After scroll stop, discard frames only.
        if (_scrollController is { HasTiles: true, IsActive: false })
        {
            _ = DiscardScrollTilesAsync();
        }
        else if (_scrollController is { IsActive: true })
        {
            _ = StopScrollCaptureAsync();
        }
        else
        {
            Cancel();
        }

        e.Handled = true;
    }

    private void OnRootPointerPressed(object sender, MouseButtonEventArgs e)
    {
        // Selection drag is left-button only (handler is MouseLeftButtonDown).
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (_dragMovingHandle)
        {
            return;
        }

        var p = e.GetPosition(_root);

        // Inside an existing selection: AnnotationCanvas (above) or a resize
        // handle on the adorner (above) owns the event — do nothing here.
        if (_hasSelection && _selection.Contains(p))
        {
            return;
        }

        _dragCreating = true;
        _dragStartPoint = p;
        _dragStartSelection = default;
        _hasSelection = false;
        _selection = new Rect(p.X, p.Y, 0, 0);
        _toolbar.Visibility = Visibility.Collapsed;
        _inputCatcher.CaptureMouse();
        e.Handled = true;
    }

    private void OnRootPointerMoved(object sender, MouseEventArgs e)
    {
        if (!_dragCreating)
        {
            return;
        }

        var p = e.GetPosition(_root);
        _selection = NormalizeRect(_dragStartPoint, p);
        UpdateSelectionVisuals();
        e.Handled = true;
    }

    private void OnRootPointerReleased(object sender, MouseButtonEventArgs e)
    {
        if (!_dragCreating)
        {
            return;
        }

        _dragCreating = false;
        _inputCatcher.ReleaseMouseCapture();
        if (_selection.Width < 5 || _selection.Height < 5)
        {
            _selection = default;
            _hasSelection = false;
            UpdateSelectionVisuals();
            return;
        }

        _hasSelection = true;
        UpdateSelectionVisuals();
        _annoCanvas.Focus();
        e.Handled = true;
    }

    // -----------------------------------------------------------------------------------
    //  Resize handles
    // -----------------------------------------------------------------------------------
    private void OnHandleDrag(object sender, SelectionAdorner.HandleDragEventArgs e)
    {
        if (e.State == SelectionAdorner.HandleDragState.Start)
        {
            _dragMovingHandle = true;
            _dragStartPoint = e.Position;
            _dragStartSelection = _selection;
        }
        else if (e.State == SelectionAdorner.HandleDragState.Move)
        {
            var p = e.Position;
            double dx = p.X - _dragStartPoint.X;
            double dy = p.Y - _dragStartPoint.Y;
            _selection = ApplyHandle(_dragStartSelection, e.Kind, dx, dy);
            UpdateSelectionVisuals();
        }
        else
        {
            _dragMovingHandle = false;
            if (_selection.Width < 5 || _selection.Height < 5)
            {
                _hasSelection = false;
                _selection = default;
                _toolbar.Visibility = Visibility.Collapsed;
                UpdateSelectionVisuals();
            }
        }
    }

    private static Rect ApplyHandle(Rect start, SelectionAdorner.HandleKind k, double dx, double dy)
    {
        double l = start.X, t = start.Y, r = start.Right, b = start.Bottom;
        switch (k)
        {
            case SelectionAdorner.HandleKind.TopLeft: l += dx; t += dy; break;
            case SelectionAdorner.HandleKind.Top: t += dy; break;
            case SelectionAdorner.HandleKind.TopRight: r += dx; t += dy; break;
            case SelectionAdorner.HandleKind.Left: l += dx; break;
            case SelectionAdorner.HandleKind.Right: r += dx; break;
            case SelectionAdorner.HandleKind.BottomLeft: l += dx; b += dy; break;
            case SelectionAdorner.HandleKind.Bottom: b += dy; break;
            case SelectionAdorner.HandleKind.BottomRight: r += dx; b += dy; break;
        }

        double x = Math.Min(l, r), y = Math.Min(t, b);
        double w = Math.Max(1, Math.Abs(r - l));
        double h = Math.Max(1, Math.Abs(b - t));
        return new Rect(x, y, w, h);
    }

    // -----------------------------------------------------------------------------------
    //  Visual update
    // -----------------------------------------------------------------------------------
    private void UpdateSelectionVisuals()
    {
        if (ShouldShowSelectionVignette())
        {
            _dimPath.Fill = _selectionDimBrush;
            var outer = new RectangleGeometry(new Rect(0, 0, _logicalSize.Width, _logicalSize.Height));
            var inner = new RectangleGeometry(_selection);
            _dimPath.Data = new CombinedGeometry(GeometryCombineMode.Xor, outer, inner);
            _dimPath.Visibility = Visibility.Visible;

            // Annotations live in capture/screen coordinates; clip to the selection for display.
            _annoCanvas.Width = _logicalSize.Width;
            _annoCanvas.Height = _logicalSize.Height;
            Canvas.SetLeft(_annoCanvas, 0);
            Canvas.SetTop(_annoCanvas, 0);
            _annoCanvas.SourceOffset = new Point(0, 0);
            _annoCanvas.Clip = new RectangleGeometry(_selection);
            _annoCanvas.Visibility = Visibility.Visible;

            _toolbar.Visibility = _hasSelection ? Visibility.Visible : Visibility.Collapsed;
            if (_hasSelection)
            {
                PositionToolbar();
            }
        }
        else
        {
            _dimPath.Fill = _idleDimBrush;
            _dimPath.Data = new RectangleGeometry(new Rect(0, 0, _logicalSize.Width, _logicalSize.Height));
            _dimPath.Visibility = Visibility.Visible;
            _annoCanvas.Visibility = Visibility.Collapsed;
            _toolbar.Visibility = Visibility.Collapsed;
        }

        _adorner.Selection = _selection;
        _adorner.IsVisible = _hasSelection || _dragCreating;
        _adorner.UpdateLayout();
    }

    private void PositionToolbar()
    {
        _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = _toolbar.DesiredSize;
        var pos = ToolbarPlacement.Compute(_selection, sz, MonitorAreaFor(_selection));
        Canvas.SetLeft(_toolbar, pos.X);
        Canvas.SetTop(_toolbar, pos.Y);
    }

    // -----------------------------------------------------------------------------------
    //  Keyboard
    // -----------------------------------------------------------------------------------
    private void OnToolbarToolChanged(object sender, string id)
    {
        _annoCanvas.ActiveTool = id;
        _annoCanvas.ClearSelection();
        Cursor = id == ToolIds.Select
            ? Cursors.Arrow
            : Cursors.Cross;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_scrollController is { IsActive: true } or { HasTiles: true })
        {
            if (e.Key == Key.Enter)
            {
                _ = FinishScrollCaptureAndConfirmAsync();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (_scrollController.IsActive)
                {
                    // Stop capturing but keep tiles (toolbar Stop / Esc while rolling).
                    _ = StopScrollCaptureAsync();
                }
                else
                {
                    // After stop: discard scroll frames only — keep selection / snip session.
                    _ = DiscardScrollTilesAsync();
                }

                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            _ = DoConfirmAsync();
            return;
        }

        if (e.Key == Key.Delete)
        {
            _annoCanvas.DeleteSelected();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key == Key.Z)
            {
                _annoCanvas.Undo();
                e.Handled = true;
            }
            else if (e.Key == Key.Y)
            {
                _annoCanvas.Redo();
                e.Handled = true;
            }
            else if (e.Key == Key.C)
            {
                _ = DoCopyAsync();
                e.Handled = true;
            }
            else if (e.Key == Key.S)
            {
                _ = DoSaveAsync();
                e.Handled = true;
            }
        }
    }

    // -----------------------------------------------------------------------------------
    //  Scroll capture mode (adapted from TDS ScreenshotWindow)
    // -----------------------------------------------------------------------------------
    private void OnScrollCaptureToggled(object sender, EventArgs e) => ToggleScrollCapture(scrollUp: false);

    private void OnScrollUpCaptureToggled(object sender, EventArgs e) => ToggleScrollCapture(scrollUp: true);

    private void ToggleScrollCapture(bool scrollUp)
    {
        try
        {
            if (_scrollController is { IsActive: true })
            {
                _ = StopScrollCaptureAsync();
            }
            else if (_hasSelection)
            {
                StartScrollCapture(scrollUp);
            }
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow scroll toggle failed: {ex.Message}", ex, GetType());
            EnsureScrollUiRecovered();
        }
    }

    private void StartScrollCapture(bool scrollUp = false)
    {
        if (!_hasSelection)
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
            // Window-capture path: overlay stays visible — show frozen Edge in the selection
            // (no Opacity=0 / no SetWindowRgn hole). Wheel still reaches underlay via
            // temporary WS_EX_TRANSPARENT in SendInput.
            _screenshotImage.Opacity = 1;
            _screenshotImage.Visibility = Visibility.Visible;
            ClearScrollCaptureHole();
            UpdateLayout();
            Log.Info(
                "Snip scroll UI: keep frozen selection preview (window-capture; avoid white hole)",
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
            if (stitched.PixelWidth < 1 || stitched.PixelHeight < 1)
            {
                return;
            }

            ApplyStitchedScrollResult(stitched);
            _scrollController = null;
            await DoConfirmAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow FinishScrollCaptureAndConfirmAsync: {ex.Message}", ex, GetType());
            EnsureScrollUiRecovered();
        }
    }

    private async Task<System.Windows.Media.Imaging.WriteableBitmap> StitchScrollTilesForExportAsync()
    {
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
            if (stitched.PixelWidth < 1 || stitched.PixelHeight < 1)
            {
                return null;
            }

            ApplyStitchedScrollResult(stitched);
            _scrollController = null;
            return stitched;
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow StitchScrollTilesForExportAsync: {ex.Message}", ex, GetType());
            EnsureScrollUiRecovered();
            return null;
        }
    }

    private void OnScrollCapturingStopped(int tileCount)
    {
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
                    _screenshotImage.Source = BitmapPreview.ForOverlay(_source);
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

            await ctl.CancelAsync().ConfigureAwait(true);
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
        try
        {
            RestorePassthroughUi();
            _toolbar.SetScrollCaptureActive(false);
            _toolbar.SetScrollTilesPending(false);
            _scrollController = null;
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow OnScrollCaptureCancelled: {ex.Message}", ex, GetType());
            EnsureScrollUiRecovered();
            _scrollController = null;
        }
    }

    private void RestorePassthroughUi()
    {
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

    // -----------------------------------------------------------------------------------
    //  Output
    // -----------------------------------------------------------------------------------
    private void Cancel()
    {
        DismissSessionChild(restoreOverlay: false);
        try
        {
            _scrollController?.Cancel();
        }
        catch (Exception ex)
        {
            // Best-effort; keep going with teardown.
            Log.Exception($"SnipWindow scroll cancel failed: {ex.Message}", ex, GetType());
        }

        _scrollController = null;
        ClearScrollCaptureHole();
        Result = new EditResult { Outcome = EditOutcome.Cancelled };
        Close();
    }

    /// <summary>
    /// Renders the selection plus annotations into a fresh bitmap whose size
    /// equals the selection's size in physical pixels.
    /// With no annotations, returns a <see cref="CroppedBitmap"/> only (no
    /// <see cref="RenderTargetBitmap"/> round-trip).
    /// </summary>
    private BitmapSource BuildResultBitmap(Rect selection)
    {
        // Selection is in logical (DIP) coords; the source bitmap is physical pixels.
        double dpi = _dpiScale;
        int x = (int)Math.Round(selection.X * dpi);
        int y = (int)Math.Round(selection.Y * dpi);
        int w = (int)Math.Round(selection.Width * dpi);
        int h = (int)Math.Round(selection.Height * dpi);
        w = Math.Max(1, Math.Min(w, _source.PixelWidth - x));
        h = Math.Max(1, Math.Min(h, _source.PixelHeight - y));

        // 1) Crop the source 1:1.
        var cropped = new CroppedBitmap(_source, new Int32Rect(x, y, w, h));

        // No markup → skip RenderTargetBitmap (crop is enough for copy/save/OCR).
        if (_annoCanvas.Items.Count == 0)
        {
            return cropped;
        }

        // 2) Render annotations on top of the 1:1 crop. AnnotationCanvas only
        //    draws markup (not the background); mosaic still samples SourceBitmap
        //    at the corresponding source pixels.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(cropped, new Rect(0, 0, w, h));
            var matrix = new Matrix(dpi, 0, 0, dpi, -selection.X * dpi, -selection.Y * dpi);
            dc.PushTransform(new MatrixTransform(matrix));
            _annoCanvas.RenderAnnotations(dc);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    private async Task DoConfirmAsync()
    {
        DismissSessionChild(restoreOverlay: false);
        var built = await BuildAndEncodeAsync().ConfigureAwait(true);
        if (built == null)
        {
            return;
        }

        Result = new EditResult
        {
            Outcome = EditOutcome.Confirmed,
            Selection = _selection,
            Annotations = _annoCanvas.Items.ToArray(),
            Result = built.Value.Bitmap,
            PngBytes = built.Value.Png,
        };
        Close();
    }

    private async Task DoCopyAsync()
    {
        DismissSessionChild(restoreOverlay: false);
        var built = await BuildAndEncodeAsync().ConfigureAwait(true);
        if (built == null)
        {
            return;
        }

        try
        {
            // CopyPixels / DIB build off the UI thread for large frames.
            await ClipboardService.CopyBitmapAsync(built.Value.Bitmap, built.Value.Png).ConfigureAwait(true);
            Result = new EditResult
            {
                Outcome = EditOutcome.Copied,
                Selection = _selection,
                Annotations = _annoCanvas.Items.ToArray(),
                Result = built.Value.Bitmap,
                PngBytes = built.Value.Png,
            };
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow DoCopyAsync failed: {ex.Message}", ex, GetType());
            Result ??= new EditResult { Outcome = EditOutcome.Cancelled };
        }

        Close();
    }

    /// <summary>
    /// Opens OCR on the current selection without ending the snip session.
    /// The overlay is hidden until the result window closes, then restored.
    /// </summary>
    private async Task DoExtractTextAsync()
    {
        if (!_hasSelection || _sessionChild != null || _exportBusy)
        {
            return;
        }

        if (!EnsureOcrAssetsReady())
        {
            return;
        }

        try
        {
            var built = await BuildAndEncodeAsync().ConfigureAwait(true);
            if (built == null)
            {
                return;
            }

            var child = new OcrResultWindow(built.Value.Bitmap, built.Value.Png, _settings);
            OpenSessionChild(child);
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow DoExtractTextAsync failed: {ex.Message}", ex, GetType());
            RestoreOverlayAfterChild();
        }
    }

    /// <summary>
    /// Opens image translate without ending the snip session. Misconfiguration
    /// keeps the overlay visible with a warning (same as before).
    /// </summary>
    private async Task DoTranslateImageAsync()
    {
        if (!_hasSelection || _sessionChild != null || _exportBusy)
        {
            return;
        }

        if (!EnsureOcrAssetsReady())
        {
            return;
        }

        if (!EnsureHyMtModelReady())
        {
            return;
        }

        var error = TranslatorFactory.Validate(_settings);
        if (error != null)
        {
            ShowOverlayMessageBox(
                error,
                Properties.Resources.tip_translate,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var built = await BuildAndEncodeAsync().ConfigureAwait(true);
            if (built == null)
            {
                return;
            }

            var child = new TranslateResultWindow(built.Value.Bitmap, built.Value.Png, _settings);
            OpenSessionChild(child);
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow DoTranslateImageAsync failed: {ex.Message}", ex, GetType());
            RestoreOverlayAfterChild();
        }
    }

    /// <summary>
    /// Ensures WeChat OCR assets are present (plugin <c>wco_data</c> or LocalAppData
    /// cache). Slim packages prompt to download ~61 MB from the WeChatOcr NuGet package.
    /// </summary>
    private bool EnsureOcrAssetsReady()
    {
        // Windows OCR (explicit, Auto without WeChat assets, or any non-x64 process) needs no download.
        if (OcrService.ResolveEngine(_settings) != OcrEngineKind.WeChat
            || WeChatOcrService.IsOcrComponentAvailable())
        {
            return true;
        }

        var answer = ShowOverlayMessageBox(
            Properties.Resources.ocr_download_prompt,
            Properties.Resources.tip_ocr,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            ShowOverlayMessageBox(
                Properties.Resources.ocr_err_need_install,
                Properties.Resources.tip_ocr,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var (ok, path, downloadError) = RunWithOverlayNotTopmost(
            () => OcrAssetsDownloadDialog.ShowDialogAndDownload(owner: this));
        if (!ok || string.IsNullOrWhiteSpace(path))
        {
            ShowOverlayMessageBox(
                downloadError ?? Properties.Resources.ocr_err_download_network,
                Properties.Resources.ocr_download_title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        return WeChatOcrService.IsOcrComponentAvailable();
    }

    /// <summary>
    /// When HyMT2Sharp is selected and no usable GGUF is configured, offer to
    /// download the recommended 2Bit model into LocalAppData and fill the path.
    /// </summary>
    private bool EnsureHyMtModelReady()
    {
        if (_settings.TranslatorProvider != TranslatorProvider.HyMT2Sharp)
        {
            return true;
        }

        var resolved = HyMtModelDownloader.ResolveGgufPath(_settings);
        if (resolved != null)
        {
            _settings.HyMtGgufPath = resolved;
            return true;
        }

        var answer = ShowOverlayMessageBox(
            Properties.Resources.tr_download_prompt,
            Properties.Resources.tip_translate,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            ShowOverlayMessageBox(
                Properties.Resources.tr_err_gguf_path,
                Properties.Resources.tip_translate,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var (ok, path, downloadError) = RunWithOverlayNotTopmost(
            () => HyMtModelDownloadDialog.ShowDialogAndDownload(
                owner: this,
                customDownloadUrl: _settings.HyMtCustomDownloadUrl));
        if (!ok || string.IsNullOrWhiteSpace(path))
        {
            ShowOverlayMessageBox(
                downloadError ?? Properties.Resources.tr_err_download_network,
                Properties.Resources.tr_download_title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        _settings.HyMtGgufPath = path;
        HyMtGgufPathPersister.Persist(path);
        return true;
    }

    /// <summary>
    /// Shows a MessageBox owned by this overlay while briefly clearing
    /// <see cref="Window.Topmost"/> so the dialog is not hidden behind the
    /// fullscreen snip window (slim OCR / HyMT download prompts).
    /// </summary>
    private MessageBoxResult ShowOverlayMessageBox(
        string message,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon)
    {
        var wasTopmost = Topmost;
        try
        {
            Topmost = false;
            return MessageBox.Show(this, message, caption, button, icon);
        }
        finally
        {
            Topmost = wasTopmost;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with overlay Topmost cleared so owned
    /// modal download dialogs stay visible above the snip chrome.
    /// </summary>
    private T RunWithOverlayNotTopmost<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var wasTopmost = Topmost;
        try
        {
            Topmost = false;
            return action();
        }
        finally
        {
            Topmost = wasTopmost;
        }
    }

    /// <summary>
    /// Pins the current selection as a floating topmost image and keeps the overlay
    /// so the user can annotate or pin again.
    /// </summary>
    private async Task DoPinAsync()
    {
        if (!_hasSelection || _sessionChild != null || _exportBusy)
        {
            return;
        }

        try
        {
            var built = await BuildAndEncodeAsync().ConfigureAwait(true);
            if (built == null)
            {
                return;
            }

            // Drop overlay topmost briefly so the new pin is not covered, then
            // re-assert both; pin stays alive independently of the snip session.
            Topmost = false;
            var pin = new PinImageWindow(built.Value.Bitmap);
            pin.Show();
            pin.Activate();
            Topmost = true;
            pin.Topmost = true;
            pin.Activate();
        }
        catch (Exception ex)
        {
            Topmost = true;
            Log.Exception($"SnipWindow DoPinAsync failed: {ex.Message}", ex, GetType());
        }
    }

    /// <summary>
    /// Hides the fullscreen overlay and shows a modeless child; when the child
    /// closes, the overlay is shown again (same capture / selection / annotations).
    /// </summary>
    private void OpenSessionChild(Window child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (_sessionChild != null)
        {
            return;
        }

        _sessionChild = child;
        child.Closed += OnSessionChildClosed;
        _hiddenForSessionChild = true;
        Hide();
        child.Show();
        child.Activate();
    }

    private void OnSessionChildClosed(object sender, EventArgs e)
    {
        if (sender is Window w)
        {
            w.Closed -= OnSessionChildClosed;
        }

        if (ReferenceEquals(_sessionChild, sender))
        {
            _sessionChild = null;
        }

        RestoreOverlayAfterChild();
    }

    private void RestoreOverlayAfterChild()
    {
        if (!_hiddenForSessionChild || _disposed)
        {
            return;
        }

        _hiddenForSessionChild = false;
        try
        {
            Show();
            Activate();
            Topmost = true;
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow restore overlay failed: {ex.Message}", ex, GetType());
        }
    }

    /// <summary>
    /// Closes any OCR/translate child. When <paramref name="restoreOverlay"/> is
    /// false (session ending), the overlay is not shown again.
    /// </summary>
    private void DismissSessionChild(bool restoreOverlay)
    {
        var child = _sessionChild;
        if (child == null)
        {
            if (!restoreOverlay)
            {
                _hiddenForSessionChild = false;
            }

            return;
        }

        _sessionChild = null;
        child.Closed -= OnSessionChildClosed;
        try
        {
            child.Close();
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow dismiss session child failed: {ex.Message}", ex, GetType());
        }

        if (restoreOverlay)
        {
            RestoreOverlayAfterChild();
        }
        else
        {
            _hiddenForSessionChild = false;
        }
    }

    private async Task DoSaveAsync()
    {
        if (!_hasSelection || _exportBusy)
        {
            return;
        }

        DismissSessionChild(restoreOverlay: false);
        try
        {
            var built = await BuildAndEncodeAsync().ConfigureAwait(true);
            if (built == null)
            {
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Properties.Resources.save_dialog_title,
                DefaultExt = ".png",
                FileName = ScreenshotFileSaver.BuildFileName(),
                Filter = Properties.Resources.save_dialog_filter,
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var path = dialog.FileName;
            var format = PngEncoder.FormatFromPath(path);
            if (format == ImageFileFormat.Png)
            {
                await File.WriteAllBytesAsync(path, built.Value.Png).ConfigureAwait(true);
            }
            else
            {
                await PngEncoder.EncodeToFileAsync(built.Value.Bitmap, path, format).ConfigureAwait(true);
            }

            Result = new EditResult
            {
                Outcome = EditOutcome.Saved,
                Selection = _selection,
                Annotations = _annoCanvas.Items.ToArray(),
                Result = built.Value.Bitmap,
                PngBytes = built.Value.Png,
                SavedPath = path,
            };
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow DoSaveAsync failed: {ex.Message}", ex, GetType());
            Result ??= new EditResult { Outcome = EditOutcome.Cancelled };
        }

        Close();
    }

    /// <summary>
    /// Composites the selection on the UI thread (RenderTargetBitmap only when
    /// annotations exist; otherwise a CroppedBitmap), then PNG-encodes on the
    /// thread pool. Full-res source is always used — OCR / translate / save must
    /// not see the overlay preview downsample.
    /// </summary>
    private async Task<(BitmapSource Bitmap, byte[] Png)?> BuildAndEncodeAsync()
    {
        if (!_hasSelection || _exportBusy)
        {
            return null;
        }

        _exportBusy = true;
        Cursor = Cursors.Wait;
        try
        {
            if (_scrollController is { HasTiles: true } or { IsActive: true })
            {
                var stitched = await StitchScrollTilesForExportAsync().ConfigureAwait(true);
                if (stitched == null)
                {
                    return null;
                }
            }

            var bmp = BuildResultBitmap(_selection);
            var png = await PngEncoder.EncodeAsync(bmp).ConfigureAwait(true);
            return (bmp, png);
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow BuildAndEncodeAsync failed: {ex.Message}", ex, GetType());
            Result ??= new EditResult { Outcome = EditOutcome.Cancelled };
            Close();
            return null;
        }
        finally
        {
            _exportBusy = false;
            Cursor = Cursors.Cross;
        }
    }

    // -----------------------------------------------------------------------------------
    //  Cleanup
    // -----------------------------------------------------------------------------------
    private void CleanupResources()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _scrollController?.Cancel();
        }
        catch (Exception ex)
        {
            // Best-effort; keep going with teardown.
            Log.Exception($"SnipWindow scroll cancel failed: {ex.Message}", ex, GetType());
        }

        _scrollController = null;
        ClearScrollCaptureHole();

        // Drop all references to the full-screen capture; WPF bitmaps are
        // reclaimed by the GC (they can be tens of MB).
        _screenshotImage.Source = null;
        _annoCanvas.SourceBitmap = null;
    }

    // -----------------------------------------------------------------------------------
    //  Util
    // -----------------------------------------------------------------------------------
    private static Rect NormalizeRect(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var w = Math.Max(1, Math.Abs(a.X - b.X));
        var h = Math.Max(1, Math.Abs(a.Y - b.Y));
        return new Rect(x, y, w, h);
    }

    private void PersistAnnotationBrush(Color stroke, double width)
    {
        string hex = SnipSettings.FormatColorHex(stroke);
        _settings.LastStrokeColorHex = hex;
        _settings.LastStrokeWidth = width;
        _ = Task.Run(() =>
        {
            try
            {
                SnipSettingsPersister.Persist(
                [
                    SnipSettingsPersister.OptionPatch.Text("LastStrokeColorHex", hex),
                    SnipSettingsPersister.OptionPatch.Number("LastStrokeWidth", width),
                ]);
            }
            catch
            {
                // Best-effort persistence of last brush style.
            }
        });
    }
}
