// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Compact icon button with rounded chrome, hover/press/active feedback.
/// </summary>
public sealed class ToolbarButton : Border
{
    private static readonly Color Accent = Color.FromRgb(72, 210, 110);
    private static readonly Color AccentDark = Color.FromRgb(48, 175, 85);
    private static readonly Color IconDefault = Color.FromRgb(210, 210, 214);
    private static readonly Color IconActive = Color.FromRgb(230, 255, 236);

    private readonly Path _icon;
    private bool _pressed;
    private bool _hover;

    public ToolbarButton()
    {
        Width = 28;
        Height = 28;
        MinWidth = 28;
        MinHeight = 28;
        Padding = new Thickness(5);
        CornerRadius = new CornerRadius(6);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Cursor = Cursors.Hand;
        Focusable = false;

        _icon = new Path
        {
            Stretch = Stretch.Uniform,
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        Child = _icon;

        UpdateChrome();
    }

    public event EventHandler Click;

    /// <summary>SVG-style path geometry data for the icon. Empty clears the icon.</summary>
    public string Geometry
    {
        get => field;
        set
        {
            field = value;
            RebuildIcon();
        }
    }

    public bool IsActive
    {
        get => field;
        set
        {
            field = value;
            UpdateChrome();
        }
    }

    public bool IsAccent
    {
        get => field;
        set
        {
            field = value;
            UpdateChrome();
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        _hover = true;
        UpdateChrome();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = false;
        _pressed = false;
        UpdateChrome();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled)
        {
            return;
        }

        _pressed = true;
        UpdateChrome();
        Click?.Invoke(this, EventArgs.Empty);
        CaptureMouse();
        e.Handled = true;
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _pressed = false;
        ReleaseMouseCapture();
        UpdateChrome();
        e.Handled = true;
        base.OnMouseLeftButtonUp(e);
    }

    private void RebuildIcon()
    {
        if (string.IsNullOrEmpty(Geometry) || _icon == null)
        {
            return;
        }

        _icon.Data = System.Windows.Media.Geometry.Parse(Geometry);
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        if (_icon == null)
        {
            return;
        }

        if (IsAccent)
        {
            Background = new SolidColorBrush(_pressed ? AccentDark : Accent);
            BorderBrush = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            _icon.Stroke = Brushes.White;
            _icon.Fill = null;
            return;
        }

        if (IsActive)
        {
            Background = new SolidColorBrush(Color.FromArgb(48, 72, 210, 110));
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 72, 210, 110));
            BorderThickness = new Thickness(1);
            _icon.Stroke = new SolidColorBrush(IconActive);
        }
        else if (_pressed)
        {
            Background = new SolidColorBrush(Color.FromArgb(55, 200, 200, 210));
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 200, 200, 210));
            BorderThickness = new Thickness(1);
            _icon.Stroke = new SolidColorBrush(IconDefault);
        }
        else if (_hover)
        {
            Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 200, 200, 210));
            BorderThickness = new Thickness(1);
            _icon.Stroke = new SolidColorBrush(Color.FromRgb(230, 230, 235));
        }
        else
        {
            Background = Brushes.Transparent;
            BorderBrush = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            _icon.Stroke = new SolidColorBrush(IconDefault);
        }

        _icon.Fill = null;
    }
}
