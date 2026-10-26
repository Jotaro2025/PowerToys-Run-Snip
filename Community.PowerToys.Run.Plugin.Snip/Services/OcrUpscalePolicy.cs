// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Pure policy + geometry for the small-image OCR enhancement: tiny selections
/// (short side below <see cref="MinShortSide"/>) are upscaled before recognition
/// because both engines under-recognize small text, and recognized coordinates
/// are mapped back to source-pixel space so text extraction boxes, the translate
/// renderer and the export geometry all stay aligned with the original image.
/// </summary>
public static class OcrUpscalePolicy
{
    /// <summary>Short side below which an image is upscaled before OCR.</summary>
    internal const int MinShortSide = 128;

    /// <summary>Upscale factor for small images.</summary>
    internal const double Scale = 2.0;

    /// <summary>Never upscale beyond this on the short side (guards pathological strips).</summary>
    internal const int MaxUpscaledShortSide = 1024;

    /// <summary>
    /// True when an image of this size should be upscaled before OCR. A 1–2 px
    /// strip cannot carry text and is excluded.
    /// </summary>
    internal static bool NeedsUpscale(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        int shortSide = Math.Min(width, height);
        return shortSide < MinShortSide && shortSide >= 3;
    }

    /// <summary>
    /// Target size after upscaling, or null when no upscale applies. The factor
    /// is clamped so the short side does not exceed <see cref="MaxUpscaledShortSide"/>.
    /// </summary>
    internal static (int Width, int Height)? PlanUpscale(int width, int height)
    {
        if (!NeedsUpscale(width, height))
        {
            return null;
        }

        int shortSide = Math.Min(width, height);
        double scale = Math.Min(Scale, MaxUpscaledShortSide / (double)shortSide);
        scale = Math.Max(scale, 1.0);
        return ((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    /// <summary>Actual factor the plan produces for a source size (target / source on the short side).</summary>
    internal static double ActualScale(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        int sourceShort = Math.Min(sourceWidth, sourceHeight);
        int targetShort = Math.Min(targetWidth, targetHeight);
        return sourceShort > 0 ? targetShort / (double)sourceShort : 1.0;
    }

    /// <summary>
    /// Maps a recognized box from upscaled space back to source space
    /// (divide by the upscale factor).
    /// </summary>
    internal static Rect MapBox(Rect box, double scale)
    {
        double inv = scale > 0.0001 ? 1.0 / scale : 1.0;
        return new Rect(box.X * inv, box.Y * inv, box.Width * inv, box.Height * inv);
    }

    /// <summary>Maps corner points from upscaled space back to source space.</summary>
    internal static IReadOnlyList<Point> MapCorners(IReadOnlyList<Point> corners, double scale)
    {
        if (corners == null || corners.Count < 4)
        {
            return null;
        }

        double inv = scale > 0.0001 ? 1.0 / scale : 1.0;
        return
        [
            new Point(corners[0].X * inv, corners[0].Y * inv),
            new Point(corners[1].X * inv, corners[1].Y * inv),
            new Point(corners[2].X * inv, corners[2].Y * inv),
            new Point(corners[3].X * inv, corners[3].Y * inv),
        ];
    }
}
