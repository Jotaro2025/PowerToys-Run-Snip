// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Lightweight Snipaste-style pin: topmost thin-border window showing a still
/// screenshot. Drag to move; mouse wheel zooms; Ctrl+wheel changes opacity;
/// Esc, double-click, or context menu closes it.
/// The UI holds only a downsampled/frozen display copy — not the export full-res.
/// </summary>
public sealed class PinImageWindow : Window
{
    internal const double MinZoom = 0.25;
    internal const double MaxZoom = 8.0;
    internal const double MinOpacity = 0.15;
    internal const double MaxOpacity = 1.0;
    private const double ZoomStep = 1.1;
    private const double OpacityStep = 0.05;

    private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
    private Point _dragStart;
    private bool _dragging;
    private double _zoom = 1.0;

    public PinImageWindow(BitmapSource image)
    {
        ArgumentNullException.ThrowIfNull(image);

        Title = Properties.Resources.pin_window_title;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = true;
        Background = new SolidColorBrush(Color.FromRgb(32, 32, 36));
        BorderBrush = new SolidColorBrush(Color.FromRgb(72, 210, 110));
        BorderThickness = new Thickness(1);
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.SizeAll;
        Opacity = 1.0;

        // Materialized display bitmap (may be downsampled). Do not keep full-res as Source.
        BitmapSource display = BitmapPreview.ForPin(image);

        var imageView = new Image
        {
            Source = display,
            Stretch = Stretch.None,
            SnapsToDevicePixels = true,
            LayoutTransform = _scale,
        };
        RenderOptions.SetBitmapScalingMode(imageView, BitmapScalingMode.NearestNeighbor);

        Content = imageView;

        var menu = new ContextMenu();
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
        MouseDoubleClick += (sender, e) =>
        {
            Close();
            e.Handled = true;
        };

        Loaded += (sender, e) => PlaceNearCursor();
    }

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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
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
