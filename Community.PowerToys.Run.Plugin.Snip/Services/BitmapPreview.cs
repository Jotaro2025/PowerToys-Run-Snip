// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Builds a lighter overlay/preview bitmap for large (near-fullscreen) captures.
/// Full-resolution source stays used for save / copy / OCR / translate / mosaic / pin.
/// </summary>
public static class BitmapPreview
{
    /// <summary>
    /// Longest edge (physical pixels) above which the overlay is downsampled.
    /// 1920 keeps a 1080p panel 1:1 and shrinks 4K / multi-monitor virtual desktops.
    /// </summary>
    public const int MaxPreviewEdge = 1920;

    /// <summary>
    /// Fraction of the monitor work area a pin window may occupy initially (DIP), matching
    /// <c>PinImageWindow</c> layout caps.
    /// </summary>
    public const double PinWorkAreaFraction = 0.9;

    /// <summary>
    /// Returns <paramref name="fullRes"/> unchanged when small enough; otherwise a
    /// materialized, frozen bitmap scaled so the longest edge is
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
        return MaterializeScaled(fullRes, scale);
    }

    /// <summary>
    /// Materializes and freezes a full-resolution independent copy for the pin window
    /// so zooming in, copying, and saving retain full original clarity (especially for
    /// scrolled long screenshots) without aliasing the caller's bitmap instance.
    /// </summary>
    public static BitmapSource ForPin(BitmapSource fullRes)
    {
        ArgumentNullException.ThrowIfNull(fullRes);
        return MaterializeFrozen(fullRes);
    }

    /// <summary>
    /// Computes the initial zoom factor (<c>&lt;= 1.0</c>) so a pinned image of size
    /// <paramref name="pixelWidth"/>×<paramref name="pixelHeight"/> fits within
    /// <see cref="PinWorkAreaFraction"/> of the monitor work area while keeping its
    /// full-resolution backing <see cref="BitmapSource"/>.
    /// </summary>
    public static double ComputeInitialPinZoom(
        int pixelWidth,
        int pixelHeight,
        double dpiX,
        double dpiY,
        double workAreaDipWidth,
        double workAreaDipHeight,
        double minZoom = 0.1)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return 1.0;
        }

        double effDpiX = dpiX > 0 ? dpiX : 96.0;
        double effDpiY = dpiY > 0 ? dpiY : 96.0;
        double dipW = pixelWidth * 96.0 / effDpiX;
        double dipH = pixelHeight * 96.0 / effDpiY;

        double maxDipW = Math.Max(1.0, workAreaDipWidth * PinWorkAreaFraction);
        double maxDipH = Math.Max(1.0, workAreaDipHeight * PinWorkAreaFraction);

        double scale = Math.Min(1.0, Math.Min(maxDipW / dipW, maxDipH / dipH));
        if (scale >= 0.999)
        {
            return 1.0;
        }

        return Math.Clamp(scale, minZoom, 1.0);
    }

    /// <summary>True when <see cref="ForOverlay"/> would return a downsampled image.</summary>
    public static bool NeedsDownsample(int pixelWidth, int pixelHeight)
        => Math.Max(pixelWidth, pixelHeight) > MaxPreviewEdge;

    /// <summary>
    /// Evaluates <paramref name="source"/> into a frozen <see cref="WriteableBitmap"/>
    /// so the first WPF compose is not deferred through a lazy transform chain.
    /// </summary>
    public static WriteableBitmap MaterializeFrozen(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var wb = new WriteableBitmap(source);
        if (wb.CanFreeze)
        {
            wb.Freeze();
        }

        return wb;
    }

    private static WriteableBitmap MaterializeScaled(BitmapSource fullRes, double scale)
    {
        var scaled = new TransformedBitmap(fullRes, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return MaterializeFrozen(scaled);
    }
}
