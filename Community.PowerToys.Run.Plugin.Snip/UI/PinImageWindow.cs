// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Lightweight Snipaste-style pin: topmost thin-border window showing a still
/// screenshot at full original bitmap resolution. Drag to move; mouse wheel zooms;
/// Ctrl+wheel changes opacity; Ctrl+C copies; Ctrl+S saves; Ctrl+0 resets 1:1;
/// Ctrl+T toggles mouse click-through; Esc, double-click, or context menu closes it.
/// </summary>
public sealed class PinImageWindow : Window
{
    internal const double MinZoom = 0.10;
    internal const double MaxZoom = 8.0;
    internal const double MinOpacity = 0.15;
    internal const double MaxOpacity = 1.0;
    internal const double CrispPixelZoomThreshold = 2.0;
    private const double ZoomStep = 1.1;
    private const double OpacityStep = 0.05;

    private static readonly SolidColorBrush NormalBorderBrush = new(Color.FromRgb(72, 210, 110));
    private static readonly SolidColorBrush ClickThroughBorderBrush = new(Color.FromRgb(56, 189, 248));

    private readonly BitmapSource _bitmap;
    private readonly Image _imageView;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly MenuItem _clickThroughMenuItem;
    private Point _dragStart;
    private bool _dragging;
    private double _zoom = 1.0;
    private bool _isClickThrough;
    private long _baseExStyle;
    private bool _baseExStyleCaptured;
    private Window _clickThroughBadge;

    public PinImageWindow(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);

        Title = Properties.Resources.pin_window_title;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = true;
        Background = new SolidColorBrush(Color.FromRgb(32, 32, 36));
        BorderBrush = NormalBorderBrush;
        BorderThickness = new Thickness(1);
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.SizeAll;
        Opacity = 1.0;

        // Keep full original bitmap resolution in an independent frozen WriteableBitmap
        // while scaling initial window DIP dimensions to fit the screen work area.
        _bitmap = BitmapPreview.ForPin(image);
        _zoom = BitmapPreview.ComputeInitialPinZoom(
            _bitmap.PixelWidth,
            _bitmap.PixelHeight,
            _bitmap.DpiX,
            _bitmap.DpiY,
            SystemParameters.WorkArea.Width,
            SystemParameters.WorkArea.Height,
            MinZoom);
        _scale.ScaleX = _zoom;
        _scale.ScaleY = _zoom;

        _imageView = new Image
        {
            Source = _bitmap,
            Stretch = Stretch.None,
            SnapsToDevicePixels = true,
            LayoutTransform = _scale,
        };
        RenderOptions.SetBitmapScalingMode(_imageView, SelectScalingMode(_zoom));

        Content = _imageView;

        var menu = new ContextMenu();

        var copyItem = new MenuItem { Header = Properties.Resources.pin_menu_copy };
        copyItem.Click += async (sender, e) => await CopyImageAsync().ConfigureAwait(true);
        menu.Items.Add(copyItem);

        var saveItem = new MenuItem { Header = Properties.Resources.pin_menu_save };
        saveItem.Click += async (sender, e) => await SaveImageAsync().ConfigureAwait(true);
        menu.Items.Add(saveItem);

        var resetItem = new MenuItem { Header = Properties.Resources.pin_menu_reset_zoom };
        resetItem.Click += (sender, e) => ResetZoomAndOpacity();
        menu.Items.Add(resetItem);

        _clickThroughMenuItem = new MenuItem
        {
            Header = Properties.Resources.pin_menu_click_through,
            IsCheckable = true,
        };
        _clickThroughMenuItem.Click += (sender, e) => SetClickThrough(!_isClickThrough);
        menu.Items.Add(_clickThroughMenuItem);

        menu.Items.Add(new Separator());

        var closeItem = new MenuItem { Header = Properties.Resources.pin_menu_close };
        closeItem.Click += (sender, e) => Close();
        menu.Items.Add(closeItem);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = Properties.Resources.pin_hint_wheel,
            IsEnabled = false,
        });
        ContextMenu = menu;

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseMove += OnMouseMove;
        LocationChanged += (sender, e) => RepositionClickThroughBadge();
        SizeChanged += (sender, e) => RepositionClickThroughBadge();
        Closed += (sender, e) => CloseClickThroughBadge();
        MouseDoubleClick += (sender, e) =>
        {
            Close();
            e.Handled = true;
        };

        Loaded += (sender, e) => PlaceNearCursor();
    }

    /// <summary>Gets a value indicating whether mouse click-through is currently active.</summary>
    public bool IsClickThrough => _isClickThrough;

    /// <summary>Applies a mouse-wheel zoom factor; clamps to <see cref="MinZoom"/>..<see cref="MaxZoom"/>.</summary>
    internal static double ApplyZoom(double current, int wheelDelta)
    {
        double factor = wheelDelta > 0 ? ZoomStep : 1.0 / ZoomStep;
        return Math.Clamp(current * factor, MinZoom, MaxZoom);
    }

    /// <summary>Applies a Ctrl+wheel opacity step; clamps to <see cref="MinOpacity"/>..<see cref="MaxOpacity"/>.</summary>
    internal static double ApplyOpacity(double current, int wheelDelta)
    {
        double step = wheelDelta > 0 ? OpacityStep : -OpacityStep;
        return Math.Clamp(current + step, MinOpacity, MaxOpacity);
    }

    /// <summary>
    /// Chooses <see cref="BitmapScalingMode.HighQuality"/> when downscaled or near 1:1
    /// for smooth text/edges, and <see cref="BitmapScalingMode.NearestNeighbor"/> when
    /// zoomed in (<c>&gt;= 2.0x</c>) for crisp pixel inspection.
    /// </summary>
    internal static BitmapScalingMode SelectScalingMode(double zoom)
        => zoom >= CrispPixelZoomThreshold
            ? BitmapScalingMode.NearestNeighbor
            : BitmapScalingMode.HighQuality;

    /// <summary>
    /// Computes the Win32 extended window style for toggling mouse click-through.
    /// Enabling sets both <see cref="Win32.WsExLayered"/> and <see cref="Win32.WsExTransparent"/>;
    /// disabling restores the original <see cref="Win32.WsExLayered"/> bit and clears
    /// <see cref="Win32.WsExTransparent"/>.
    /// </summary>
    internal static long ComputeClickThroughExStyle(long baseExStyle, long currentExStyle, bool enable)
    {
        if (enable)
        {
            return currentExStyle | Win32.WsExLayered | Win32.WsExTransparent;
        }

        long cleared = currentExStyle & ~(long)(Win32.WsExLayered | Win32.WsExTransparent);
        long originalLayered = baseExStyle & Win32.WsExLayered;
        return cleared | originalLayered;
    }

    /// <summary>Resets zoom to 1:1 original resolution and opacity to 100%.</summary>
    public void ResetZoomAndOpacity()
    {
        _zoom = 1.0;
        _scale.ScaleX = 1.0;
        _scale.ScaleY = 1.0;
        Opacity = 1.0;
        RenderOptions.SetBitmapScalingMode(_imageView, SelectScalingMode(_zoom));
    }

    /// <summary>Enables or disables mouse click-through (<c>WS_EX_TRANSPARENT</c>) on this pin window.</summary>
    public void SetClickThrough(bool enable)
    {
        if (_isClickThrough == enable)
        {
            return;
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            long current = Win32.GetWindowLongPtr(hwnd, Win32.GwlExStyle).ToInt64();
            if (!_baseExStyleCaptured)
            {
                _baseExStyle = current;
                _baseExStyleCaptured = true;
            }

            long updated = ComputeClickThroughExStyle(_baseExStyle, current, enable);
            Win32.SetWindowLongPtr(hwnd, Win32.GwlExStyle, new IntPtr(updated));
        }

        _isClickThrough = enable;
        _clickThroughMenuItem.IsChecked = enable;
        BorderBrush = enable ? ClickThroughBorderBrush : NormalBorderBrush;

        if (enable)
        {
            ShowClickThroughBadge();
        }
        else
        {
            CloseClickThroughBadge();
        }
    }

    private async Task CopyImageAsync()
    {
        try
        {
            await ClipboardService.CopyBitmapAsync(_bitmap).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Exception($"Pin window copy image failed: {ex.Message}", ex, GetType());
        }
    }

    private async Task SaveImageAsync()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Properties.Resources.save_dialog_title,
                DefaultExt = ".png",
                FileName = ScreenshotFileSaver.BuildFileName(),
                Filter = Properties.Resources.save_dialog_filter,
                AddExtension = true,
                OverwritePrompt = true,
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string path = dialog.FileName;
            var format = PngEncoder.FormatFromPath(path);
            await PngEncoder.EncodeToFileAsync(_bitmap, path, format).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Exception($"Pin window save image failed: {ex.Message}", ex, GetType());
        }
    }

    private void ShowClickThroughBadge()
    {
        if (_clickThroughBadge is not null)
        {
            RepositionClickThroughBadge();
            return;
        }

        var btn = new Button
        {
            Content = Properties.Resources.pin_badge_exit_click_through,
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 11,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(220, 20, 24, 32)),
            BorderBrush = ClickThroughBorderBrush,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        btn.Click += (sender, e) => SetClickThrough(false);

        var badge = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = btn,
        };
        badge.PreviewKeyDown += (sender, e) =>
        {
            if (e.Key == Key.Escape)
            {
                SetClickThrough(false);
                e.Handled = true;
            }
        };

        _clickThroughBadge = badge;
        badge.Show();
        RepositionClickThroughBadge();
    }

    private void RepositionClickThroughBadge()
    {
        if (_clickThroughBadge is null)
        {
            return;
        }

        double badgeW = _clickThroughBadge.ActualWidth > 0 ? _clickThroughBadge.ActualWidth : 88;
        _clickThroughBadge.Left = Left + Math.Max(0, ActualWidth - badgeW);
        _clickThroughBadge.Top = Math.Max(0, Top - 26);
    }

    private void CloseClickThroughBadge()
    {
        if (_clickThroughBadge is null)
        {
            return;
        }

        var badge = _clickThroughBadge;
        _clickThroughBadge = null;
        try
        {
            badge.Close();
        }
        catch (Exception ex)
        {
            Log.Exception($"Failed to close click-through badge: {ex.Message}", ex, GetType());
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            Opacity = ApplyOpacity(Opacity, e.Delta);
        }
        else
        {
            _zoom = ApplyZoom(_zoom, e.Delta);
            _scale.ScaleX = _zoom;
            _scale.ScaleY = _zoom;
            RenderOptions.SetBitmapScalingMode(_imageView, SelectScalingMode(_zoom));
        }

        e.Handled = true;
    }

    private void PlaceNearCursor()
    {
        double cursorX = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width / 2);
        double cursorY = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height / 2);
        double dpi = 1.0;
        Rect workDip = SystemParameters.WorkArea;

        try
        {
            var capture = new Win32CaptureService();
            var screens = capture.GetScreens();
            var screen = Win32CaptureService.GetScreenAtCursor(screens);
            dpi = screen.DpiScale <= 0 ? 1.0 : screen.DpiScale;
            workDip = new Rect(
                screen.WorkingArea.X / dpi,
                screen.WorkingArea.Y / dpi,
                screen.WorkingArea.Width / dpi,
                screen.WorkingArea.Height / dpi);

            if (Win32.GetCursorPos(out var pt))
            {
                cursorX = pt.X / dpi;
                cursorY = pt.Y / dpi;
            }
        }
        catch (Exception ex)
        {
            // Fall back to primary work area / center.
            Log.Exception($"Pin window placement probe failed: {ex.Message}", ex, GetType());
        }

        double initialZoom = BitmapPreview.ComputeInitialPinZoom(
            _bitmap.PixelWidth,
            _bitmap.PixelHeight,
            _bitmap.DpiX,
            _bitmap.DpiY,
            workDip.Width,
            workDip.Height,
            MinZoom);
        if (Math.Abs(initialZoom - _zoom) > 1e-4)
        {
            _zoom = initialZoom;
            _scale.ScaleX = _zoom;
            _scale.ScaleY = _zoom;
            RenderOptions.SetBitmapScalingMode(_imageView, SelectScalingMode(_zoom));
            UpdateLayout();
        }

        double left = cursorX + 12;
        double top = cursorY + 12;

        if (left + ActualWidth > workDip.Right)
        {
            left = Math.Max(workDip.Left, workDip.Right - ActualWidth);
        }

        if (top + ActualHeight > workDip.Bottom)
        {
            top = Math.Max(workDip.Top, workDip.Bottom - ActualHeight);
        }

        Left = left;
        Top = top;
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_isClickThrough)
            {
                SetClickThrough(false);
            }
            else
            {
                Close();
            }

            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key == Key.C)
            {
                e.Handled = true;
                await CopyImageAsync().ConfigureAwait(true);
            }
            else if (e.Key == Key.S)
            {
                e.Handled = true;
                await SaveImageAsync().ConfigureAwait(true);
            }
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0)
            {
                e.Handled = true;
                ResetZoomAndOpacity();
            }
            else if (e.Key == Key.T)
            {
                e.Handled = true;
                SetClickThrough(!_isClickThrough);
            }
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1)
        {
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var screen = PointToScreen(e.GetPosition(this));

        // PointToScreen returns device pixels; Window Left/Top are DIPs.
        var source = PresentationSource.FromVisual(this);
        double dx = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double dy = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
        Left = (screen.X * dx) - _dragStart.X;
        Top = (screen.Y * dy) - _dragStart.Y;
    }
}
