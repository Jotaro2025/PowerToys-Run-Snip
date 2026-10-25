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
    /// 7680 covers 4K and 5K single monitors 1:1 (a 3840-width panel downscaled
    /// to 1920 made in-selection text look compressed vs the real desktop);
    /// only extreme multi-monitor virtual desktops still downsample.
    /// </summary>
    public const int MaxPreviewEdge = 7680;

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
    /// Copies <paramref name="source"/> pixels into a fresh frozen
    /// <see cref="WriteableBitmap"/>. Never Freeze()s the input: freezing a
    /// chained source (CroppedBitmap / TransformedBitmap) cascades into the
    /// caller's live WriteableBitmap and breaks its later Lock(). The explicit
    /// byte copy also guarantees the GPU compositor sees real pixels — building
    /// a WriteableBitmap from an unfrozen lazy chain renders blank on D3D.
    /// Same construction pattern as CapturedFrame.ToWriteableBitmap.
    /// </summary>
    public static WriteableBitmap MaterializeFrozen(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        int w = source.PixelWidth;
        int h = source.PixelHeight;
        if (w <= 0 || h <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Source must have positive size.");
        }

        // Normalize to BGRA32; CopyPixels from a lazily-chained source reads
        // through the chain, so no Freeze on the input is needed.
        var evaluated = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int stride = w * 4;
        var pixels = new byte[checked(stride * h)];
        evaluated.CopyPixels(pixels, stride, 0);

        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.Lock();
        try
        {
            unsafe
            {
                pixels.AsSpan()
                    .CopyTo(new Span<byte>((void*)wb.BackBuffer, pixels.Length));
            }

            wb.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            wb.Unlock();
        }

        if (wb.CanFreeze)
        {
            wb.Freeze();
        }

        return wb;
    }

    private static WriteableBitmap MaterializeScaled(BitmapSource fullRes, double scale)
    {
        // Render the scale transform into a fresh BGRA32 buffer via CopyPixels —
        // the TransformedBitmap itself is never Frozen (would cascade into the
        // caller's live source) and never fed to a WriteableBitmap constructor
        // (unfrozen chains render blank on the D3D compositor).
        var scaled = new TransformedBitmap(fullRes, new ScaleTransform(scale, scale));
        int w = scaled.PixelWidth;
        int h = scaled.PixelHeight;
        int stride = w * 4;
        var pixels = new byte[checked(stride * h)];
        scaled.CopyPixels(pixels, stride, 0);

        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.Lock();
        try
        {
            unsafe
            {
                pixels.AsSpan()
                    .CopyTo(new Span<byte>((void*)wb.BackBuffer, pixels.Length));
            }

            wb.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            wb.Unlock();
        }

        if (wb.CanFreeze)
        {
            wb.Freeze();
        }

        return wb;
    }
}
