// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Services;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Composites the export bitmap for a selection: a 1:1 <see cref="CroppedBitmap"/>
/// of the physical-pixel source, plus annotations rendered on top via
/// <see cref="RenderTargetBitmap"/> only when markup exists (no markup → no RTB
/// pass, which copy/save/OCR skip). PNG encoding runs on the thread pool
/// (<see cref="PngEncoder.EncodeAsync"/>).
/// </summary>
internal static class SnipExportComposer
{
    /// <summary>
    /// Builds the selection bitmap. Must run on the UI thread: the source bitmap
    /// and the annotation canvas are Dispatcher-affine.
    /// </summary>
    /// <param name="source">Full-resolution source (physical pixels, UI-thread owned).</param>
    /// <param name="annotations">Markup canvas whose items render on top of the crop.</param>
    /// <param name="selection">Selection in logical (DIP) coordinates.</param>
    /// <param name="dpiScale">DIP→physical scale of the overlay.</param>
    public static BitmapSource BuildResultBitmap(
        BitmapSource source,
        AnnotationCanvas annotations,
        Rect selection,
        double dpiScale)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(annotations);

        // Selection is in logical (DIP) coords; the source bitmap is physical pixels.
        double dpi = dpiScale;
        int x = (int)Math.Round(selection.X * dpi);
        int y = (int)Math.Round(selection.Y * dpi);
        int w = (int)Math.Round(selection.Width * dpi);
        int h = (int)Math.Round(selection.Height * dpi);
        w = Math.Max(1, Math.Min(w, source.PixelWidth - x));
        h = Math.Max(1, Math.Min(h, source.PixelHeight - y));

        // 1) Crop the source 1:1.
        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));

        // No markup → skip RenderTargetBitmap (crop is enough for copy/save/OCR).
        if (annotations.Items.Count == 0)
        {
            return cropped;
        }

        // 2) Render annotations on top of the 1:1 crop. AnnotationCanvas only
        //    draws markup (not the background); mosaic still samples its SourceBitmap
        //    at the corresponding source pixels.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(cropped, new Rect(0, 0, w, h));
            var matrix = new Matrix(dpi, 0, 0, dpi, -selection.X * dpi, -selection.Y * dpi);
            dc.PushTransform(new MatrixTransform(matrix));
            annotations.RenderAnnotations(dc);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }
}
