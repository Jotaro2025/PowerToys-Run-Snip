// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
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
/// and annotation UI. Closes itself when the user confirms / saves / cancels.
/// </summary>
public sealed class SnipWindow : Window
{
    private static readonly Color ChromeColor = Color.FromRgb(80, 220, 100);

    /// <summary>Subtle hint that screenshot mode is active (no selection yet).</summary>
    private readonly SolidColorBrush _idleDimBrush = new SolidColorBrush(Color.FromArgb(88, 0, 0, 0));

    /// <summary>Stronger dim outside the selected region.</summary>
    private readonly SolidColorBrush _selectionDimBrush = new SolidColorBrush(Color.FromArgb(210, 0, 0, 0));

    private readonly System.Windows.Media.Imaging.WriteableBitmap _source;
    private readonly Int32Rect _physicalBounds;
    private readonly Size _logicalSize;
    private readonly Canvas _root;

    private readonly SnipBackground _screenshotImage;
    private readonly System.Windows.Shapes.Path _dimPath;
    private readonly Border _inputCatcher;
    private readonly AnnotationCanvas _annoCanvas;
    private readonly SelectionAdorner _adorner;
    private readonly SnipToolbar _toolbar;
    private readonly SnipSettings _settings;

    private double _dpiScale;
    private Rect _selection;
    private bool _hasSelection;
    private bool _dragCreating;
    private Point _dragStartPoint;
    private Rect _dragStartSelection;
    private bool _dragMovingHandle;
    private bool _disposed;

    public SnipWindow(
        System.Windows.Media.Imaging.WriteableBitmap source,
        ScreenInfo screen,
        EditRequest request,
        SnipSettings settings = null)
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

        _screenshotImage = new SnipBackground
        {
            Source = source,
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

        _annoCanvas = new AnnotationCanvas
        {
            SourceBitmap = source,
            SourceOffset = new Point(0, 0),
            SourceDpiScale = _dpiScale,
            ActiveTool = request.InitialTool,
            CurrentStroke = request.DefaultStroke ?? Color.FromRgb(238, 32, 77),
            CurrentStrokeWidth = request.DefaultStrokeWidth,
            Width = _logicalSize.Width,
            Height = _logicalSize.Height,
            ClipToBounds = true,
            IsHitTestVisible = true,
            Focusable = true,
            Visibility = Visibility.Collapsed,
        };
        _annoCanvas.UndoCountChanged += (sender, count) => _toolbar.NotifyUndoCount(count);
        _toolbar = new SnipToolbar(request) { Visibility = Visibility.Collapsed };
        _toolbar.ToolChanged += OnToolbarToolChanged;
        _toolbar.ColorChanged += (sender, c) => _annoCanvas.CurrentStroke = c;
        _toolbar.StrokeWidthChanged += (sender, w) => _annoCanvas.CurrentStrokeWidth = w;
        _toolbar.UndoClicked += (sender, e) => _annoCanvas.Undo();
        _toolbar.SaveClicked += async (sender, e) => await DoSaveAsync();
        _toolbar.ExtractTextClicked += (sender, e) => DoExtractText();
        _toolbar.TranslateImageClicked += (sender, e) => DoTranslateImage();
        _toolbar.PinClicked += (sender, e) => DoPin();
        _toolbar.CancelClicked += (sender, e) => Cancel();
        _toolbar.ConfirmClicked += (sender, e) => DoConfirm();

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
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;

        // Initial state: light full-screen dim until the user drags a real selection.
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
        CleanupResources();
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
    private void OnRootPointerPressed(object sender, MouseButtonEventArgs e)
    {
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
        var center = AnnotationEditor.Center(_selection);
        double x = center.X - (sz.Width / 2);
        double y = _selection.Bottom + 12;
        if (y + sz.Height > _logicalSize.Height - 8)
        {
            y = _selection.Y - sz.Height - 12;
        }

        if (y < 8)
        {
            y = 8;
        }

        if (x < 8)
        {
            x = 8;
        }

        if (x + sz.Width > _logicalSize.Width - 8)
        {
            x = _logicalSize.Width - sz.Width - 8;
        }

        Canvas.SetLeft(_toolbar, x);
        Canvas.SetTop(_toolbar, y);
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
        if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            DoConfirm();
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
            else if (e.Key == Key.C)
            {
                DoCopy();
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
    //  Output
    // -----------------------------------------------------------------------------------
    private void Cancel()
    {
        Result = new EditResult { Outcome = EditOutcome.Cancelled };
        Close();
    }

    /// <summary>
    /// Renders the selection plus annotations into a fresh bitmap whose size
    /// equals the selection's size in physical pixels.
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

        // 1) Crop the source 1:1 to a fresh bitmap.
        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(
            _source,
            new Int32Rect(x, y, w, h));

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

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    private void DoConfirm()
    {
        if (!_hasSelection)
        {
            return;
        }

        try
        {
            var bmp = BuildResultBitmap(_selection);

            // Encode on this thread: the bitmap belongs to it, and WPF would
            // reject cross-thread pixel access from a thread-pool worker.
            var png = PngEncoder.Encode(bmp);
            Result = new EditResult
            {
                Outcome = EditOutcome.Confirmed,
                Selection = _selection,
                Annotations = _annoCanvas.Items.ToArray(),
                Result = bmp,
                PngBytes = png,
            };
        }
        catch (Exception ex)
        {
            // Never leave the user staring at a frozen overlay: always close
            // and surface a Cancelled result so the caller returns cleanly.
            Log.Exception($"SnipWindow DoConfirm failed: {ex.Message}", ex, GetType());
            Result ??= new EditResult { Outcome = EditOutcome.Cancelled };
        }

        Close();
    }

    private void DoCopy()
    {
        if (!_hasSelection)
        {
            return;
        }

        try
        {
            var bmp = BuildResultBitmap(_selection);

            // Encode on this thread: the bitmap belongs to it, and WPF would
            // reject cross-thread pixel access from a thread-pool worker.
            var png = PngEncoder.Encode(bmp);
            ClipboardService.CopyBitmap(bmp);
            Result = new EditResult
            {
                Outcome = EditOutcome.Copied,
                Selection = _selection,
                Annotations = _annoCanvas.Items.ToArray(),
                Result = bmp,
                PngBytes = png,
            };
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow DoCopy failed: {ex.Message}", ex, GetType());
            Result ??= new EditResult { Outcome = EditOutcome.Cancelled };
        }

        Close();
    }

    /// <summary>
    /// Hands the selection (with annotations) over for text extraction; the
    /// controller shows the result window once this overlay is closed.
    /// </summary>
    private void DoExtractText()
    {
        if (!_hasSelection)
        {
            return;
        }

        try
        {
            var bmp = BuildResultBitmap(_selection);

            // Encode on this thread: the bitmap belongs to it, and WPF would
            // reject cross-thread pixel access from a thread-pool worker.
            var png = PngEncoder.Encode(bmp);
            Result = new EditResult
            {
                Outcome = EditOutcome.ExtractText,
                Selection = _selection,
                Annotations = _annoCanvas.Items.ToArray(),
                Result = bmp,
                PngBytes = png,
            };
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow DoExtractText failed: {ex.Message}", ex, GetType());
            Result ??= new EditResult { Outcome = EditOutcome.Cancelled };
        }

        Close();
    }

    /// <summary>
    /// Hands the selection over for image translate. Shows a clear error and
    /// keeps the overlay open when the translator is off or misconfigured.
    /// </summary>
    private void DoTranslateImage()
    {
        if (!_hasSelection)
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
            MessageBox.Show(
                error,
                Properties.Resources.tip_translate,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var bmp = BuildResultBitmap(_selection);
            var png = PngEncoder.Encode(bmp);
            Result = new EditResult
            {
                Outcome = EditOutcome.TranslateImage,
                Selection = _selection,
                Annotations = _annoCanvas.Items.ToArray(),
                Result = bmp,
                PngBytes = png,
            };
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow DoTranslateImage failed: {ex.Message}", ex, GetType());
            Result ??= new EditResult { Outcome = EditOutcome.Cancelled };
        }

        Close();
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

        var answer = MessageBox.Show(
            Properties.Resources.tr_download_prompt,
            Properties.Resources.tip_translate,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            MessageBox.Show(
                Properties.Resources.tr_err_gguf_path,
                Properties.Resources.tip_translate,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var (ok, path, downloadError) = HyMtModelDownloadDialog.ShowDialogAndDownload(owner: this);
        if (!ok || string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(
                downloadError ?? Properties.Resources.tr_err_download_network,
                Properties.Resources.tr_download_title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        _settings.HyMtGgufPath = path;
        return true;
    }

    /// <summary>
    /// Pins the current selection as a floating topmost image, then closes the editor.
    /// </summary>
    private void DoPin()
    {
        if (!_hasSelection)
        {
            return;
        }

        try
        {
            var bmp = BuildResultBitmap(_selection);
            var png = PngEncoder.Encode(bmp);
            Result = new EditResult
            {
                Outcome = EditOutcome.Pinned,
                Selection = _selection,
                Annotations = _annoCanvas.Items.ToArray(),
                Result = bmp,
                PngBytes = png,
            };
        }
        catch (Exception ex)
        {
            Log.Exception($"SnipWindow DoPin failed: {ex.Message}", ex, GetType());
            Result ??= new EditResult { Outcome = EditOutcome.Cancelled };
        }

        Close();
    }

    private async Task DoSaveAsync()
    {
        if (!_hasSelection)
        {
            return;
        }

        try
        {
            var bmp = BuildResultBitmap(_selection);

            // Encode on this thread: the bitmap belongs to it, and WPF would
            // reject cross-thread pixel access from a thread-pool worker.
            var png = PngEncoder.Encode(bmp);

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
            await File.WriteAllBytesAsync(path, png);
            Result = new EditResult
            {
                Outcome = EditOutcome.Saved,
                Selection = _selection,
                Annotations = _annoCanvas.Items.ToArray(),
                Result = bmp,
                PngBytes = png,
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
}
