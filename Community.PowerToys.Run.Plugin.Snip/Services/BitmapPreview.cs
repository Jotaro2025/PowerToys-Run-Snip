// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Builds a lighter overlay/preview bitmap for large (near-fullscreen) captures.
/// Full-resolution source stays used for save / copy / OCR / translate / mosaic.
/// </summary>
public static class BitmapPreview
{
    /// <summary>
    /// Longest edge (physical pixels) above which the overlay is downsampled.
    /// 1920 keeps a 1080p panel 1:1 and shrinks 4K / multi-monitor virtual desktops.
    /// </summary>
    public const int MaxPreviewEdge = 1920;

    /// <summary>
    /// Returns <paramref name="fullRes"/> unchanged when small enough; otherwise a
    /// <see cref="TransformedBitmap"/> scaled so the longest edge is
    /// <see cref="MaxPreviewEdge"/>. Display-only — never feed this to OCR/export.
    /// </summary>
    public static BitmapSource ForOverlay(BitmapSource fullRes)
    {
        ArgumentNullException.ThrowIfNull(fullRes);

        int w = fullRes.PixelWidth;
        int h = fullRes.PixelHeight;
        if (w <= 0 || h <= 0)
        {
            return fullRes;
        }

        int longest = Math.Max(w, h);
        if (longest <= MaxPreviewEdge)
        {
            return fullRes;
        }

        double scale = (double)MaxPreviewEdge / longest;
        var scaled = new TransformedBitmap(fullRes, new ScaleTransform(scale, scale));
        return scaled;
    }

    /// <summary>True when <see cref="ForOverlay"/> would return a downsampled image.</summary>
    public static bool NeedsDownsample(int pixelWidth, int pixelHeight)
        => Math.Max(pixelWidth, pixelHeight) > MaxPreviewEdge;
}
