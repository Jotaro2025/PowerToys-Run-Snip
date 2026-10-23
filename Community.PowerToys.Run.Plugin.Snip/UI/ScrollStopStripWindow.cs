// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Community.PowerToys.Run.Plugin.Snip.Core;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Tiny always-on-top stop strip shown during scroll capture. Separate HWND from
/// the snip overlay so it stays visible while the overlay is session-hidden for Edge.
/// </summary>
public sealed class ScrollStopStripWindow : Window
{
    private readonly TextBlock _title;
    private readonly Button _stopButton;
    private bool _closing;

    public event Action StopClicked;

    public ScrollStopStripWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        _title = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 250)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };

        _stopButton = new Button
        {
            Content = Properties.Resources.scroll_stop_button,
            Padding = new Thickness(14, 6, 14, 6),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            Background = new SolidColorBrush(Color.FromRgb(220, 70, 70)),
            BorderThickness = new Thickness(0),
            Focusable = false,
        };
        _stopButton.Click += (sender, e) =>
        {
            e.Handled = true;
            StopClicked?.Invoke();
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(_title);
        row.Children.Add(_stopButton);

        var tip = new TextBlock
        {
            Text = Properties.Resources.scroll_stop_strip_tip,
            Foreground = new SolidColorBrush(Color.FromArgb(200, 220, 220, 230)),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(row);
        stack.Children.Add(tip);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 18, 18, 22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(200, 100, 180, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 16,
                ShadowDepth = 4,
                Opacity = 0.5,
            },
            Child = stack,
        };

        SetTileCount(0);
    }

    public void SetTileCount(int tileCount)
    {
        _title.Text = tileCount <= 0
            ? Properties.Resources.scroll_hint_title
            : Properties.Resources.scroll_hint_title + "  " + tileCount;
    }

    /// <summary>
    /// Position near the top-center of the capture area (physical pixels → DIP).
    /// Uses the monitor under the capture center so mixed-DPI secondary screens stay aligned.
    /// </summary>
    public void PlaceNearCapture(Int32Rect capturePhysicalPx, double dpiScale)
    {
        int cx = capturePhysicalPx.X + (capturePhysicalPx.Width / 2);
        int cy = capturePhysicalPx.Y + (capturePhysicalPx.Height / 2);
        double scale = DpiCoordinateMapping.EffectiveDpiScaleAtPhysical(cx, cy, dpiScale);
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX > 0.01)
            {
                // After Show, prefer this HWND's DPI (matches WPF layout on that monitor).
                scale = dpi.DpiScaleX;
            }
        }
        catch (Exception)
        {
            // Pre-Show / no composition target — keep monitor/caller scale.
        }

        double leftDip = capturePhysicalPx.X / scale;
        double topDip = capturePhysicalPx.Y / scale;
        double widthDip = capturePhysicalPx.Width / scale;

        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = Math.Max(DesiredSize.Width, 220);
        double h = Math.Max(DesiredSize.Height, 56);

        double vx = SystemParameters.VirtualScreenLeft;
        double vy = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth;
        double vh = SystemParameters.VirtualScreenHeight;

        double left = leftDip + Math.Max(0, (widthDip - w) / 2);
        double top = topDip + 16;
        left = Math.Min(Math.Max(left, vx + 8), vx + vw - w - 8);
        top = Math.Min(Math.Max(top, vy + 8), vy + vh - h - 8);

        Left = left;
        Top = top;
        Width = w;
        Height = h;
    }

    public void SafeClose()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        try
        {
            Close();
        }
        catch (Exception)
        {
            // STA may already be shutting down.
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        base.OnClosed(e);
    }
}
