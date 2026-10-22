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
    /// Fraction of the monitor work area a pin window may occupy (DIP), matching
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
    /// Display copy for the pin window: downsamples to fit ~90% of the work area
    /// when needed, then materializes + freezes so the pin UI does not retain the
    /// caller's full-resolution <see cref="BitmapSource"/>.
    /// </summary>
    public static BitmapSource ForPin(BitmapSource fullRes)
    {
        ArgumentNullException.ThrowIfNull(fullRes);

        int w = fullRes.PixelWidth;
        int h = fullRes.PixelHeight;
        if (w <= 0 || h <= 0)
        {
            return MaterializeFrozen(fullRes);
        }

        double dpiX = fullRes.DpiX > 0 ? fullRes.DpiX : 96;
        double dpiY = fullRes.DpiY > 0 ? fullRes.DpiY : 96;
        double dipW = w * 96.0 / dpiX;
        double dipH = h * 96.0 / dpiY;

        double maxDipW = SystemParameters.WorkArea.Width * PinWorkAreaFraction;
        double maxDipH = SystemParameters.WorkArea.Height * PinWorkAreaFraction;
        if (maxDipW < 1)
        {
            maxDipW = 1;
        }

        if (maxDipH < 1)
        {
            maxDipH = 1;
        }

        double scale = Math.Min(1.0, Math.Min(maxDipW / dipW, maxDipH / dipH));
        if (scale >= 0.999)
        {
            // Fits on screen 1:1 — still hand the pin a frozen copy, not the export bitmap.
            return MaterializeFrozen(fullRes);
        }

        return MaterializeScaled(fullRes, scale);
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
