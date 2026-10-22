// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Adapted from LdotJdot/TDS (MIT) ScrollHintOverlay for WPF.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Hint card shown during automatic scroll capture. Stitching happens on confirm/save.
/// </summary>
public sealed class ScrollHintOverlay : Border
{
    private readonly TextBlock _title;
    private readonly TextBlock _sub;
    private string _statsSubtitle;

    public ScrollHintOverlay()
    {
        Background = new SolidColorBrush(Color.FromArgb(220, 18, 18, 22));
        BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        BorderThickness = new Thickness(0.5);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12, 8, 12, 8);
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 12,
            ShadowDepth = 4,
            Opacity = 0.45,
        };
        IsHitTestVisible = false;

        _title = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 250)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        };
        _sub = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(190, 220, 220, 230)),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(_title);
        stack.Children.Add(_sub);
        Child = stack;

        Title = Properties.Resources.scroll_hint_title;
        Subtitle = Properties.Resources.scroll_hint_subtitle;
    }

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value ?? string.Empty;
    }

    public string Subtitle
    {
        get => _sub.Text;
        set => _sub.Text = value ?? string.Empty;
    }

    public void ShowManualCapturing(int tileCount)
    {
        BorderBrush = new SolidColorBrush(Color.FromArgb(200, 100, 180, 255));
        Title = tileCount <= 1
            ? Properties.Resources.scroll_hint_title
            : Properties.Resources.scroll_hint_title + "  " + tileCount;
        _statsSubtitle = Properties.Resources.scroll_hint_subtitle_active;
        Subtitle = _statsSubtitle;
    }

    public void ShowCapturing(int nextTileIndex)
    {
        BorderBrush = new SolidColorBrush(Color.FromArgb(200, 120, 200, 255));
        Title = Properties.Resources.scroll_hint_capturing.Replace("{0}", nextTileIndex.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal);
        Subtitle = Properties.Resources.scroll_hint_capturing_sub;
    }

    public void ShowManualStopped(int tileCount)
    {
        Title = tileCount <= 1
            ? Properties.Resources.scroll_hint_stopped
            : Properties.Resources.scroll_hint_stopped_count.Replace("{0}", tileCount.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal);
        _statsSubtitle = Properties.Resources.scroll_hint_stopped_sub;
        Subtitle = _statsSubtitle;
    }

    public void ShowStitchingInProgress()
    {
        BorderBrush = new SolidColorBrush(Color.FromArgb(200, 120, 200, 255));
        Title = Properties.Resources.scroll_hint_stitching;
        Subtitle = Properties.Resources.scroll_hint_stitching_sub;
    }
}
