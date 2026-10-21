// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Draws a bitmap by mapping the full source to the control bounds 1:1 in
/// layout space. Avoids <see cref="Controls.Image"/> Stretch/DPI layout quirks
/// on high-DPI displays.
/// </summary>
public sealed class SnipBackground : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(SnipBackground), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public BitmapSource Source
    {
        get => (BitmapSource)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var src = Source;
        if (src == null)
        {
            return;
        }

        var dest = new Rect(RenderSize);
        if (dest.Width <= 0 || dest.Height <= 0)
        {
            return;
        }

        // The background always shows the full capture; mapping it onto the
        // whole control keeps one source pixel per screen pixel.
        drawingContext.DrawImage(src, dest);
    }
}
