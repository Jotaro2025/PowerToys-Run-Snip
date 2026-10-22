// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Block mosaic sampling for annotation preview vs export.
/// Preview uses a cheap center 2×2 sample; export/commit uses full block average
/// (same look as the previous per-tile brush path).
/// </summary>
public static class MosaicBlocks
{
    /// <summary>How each mosaic tile's color is chosen from the source bitmap.</summary>
    public enum SampleMode
    {
        /// <summary>Average every pixel in the tile (export / committed paint).</summary>
        FullAverage = 0,

        /// <summary>Average up to four pixels around the tile center (drag preview).</summary>
        Preview2x2 = 1,
    }

    /// <summary>
    /// Fill a BGRA32 destination covering the half-open source region
    /// [<paramref name="x0"/>,<paramref name="x1"/>) × [<paramref name="y0"/>,<paramref name="y1"/>)
    /// with mosaic tiles of <paramref name="blockSize"/> (physical pixels).
    /// Destination size must be at least (x1-x0) × (y1-y0).
    /// </summary>
    public static unsafe void Fill(
        byte* src,
        int srcStride,
        int srcWidth,
        int srcHeight,
        byte* dest,
        int destStride,
        int x0,
        int y0,
        int x1,
        int y1,
        int blockSize,
        SampleMode mode)
    {
        if (src == null || dest == null)
        {
            throw new ArgumentNullException(src == null ? nameof(src) : nameof(dest));
        }

        if (srcWidth <= 0 || srcHeight <= 0 || srcStride < srcWidth * 4 || destStride < (x1 - x0) * 4)
        {
            return;
        }

        x0 = Math.Clamp(x0, 0, srcWidth);
        y0 = Math.Clamp(y0, 0, srcHeight);
        x1 = Math.Clamp(x1, 0, srcWidth);
        y1 = Math.Clamp(y1, 0, srcHeight);
        int bs = Math.Max(1, blockSize);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        for (int by = y0; by < y1; by += bs)
        {
            int blockH = Math.Min(bs, y1 - by);
            for (int bx = x0; bx < x1; bx += bs)
            {
                int blockW = Math.Min(bs, x1 - bx);
                SampleBlock(src, srcStride, bx, by, blockW, blockH, mode, out byte b, out byte g, out byte r);

                int destX0 = bx - x0;
                int destY0 = by - y0;
                for (int py = 0; py < blockH; py++)
                {
                    byte* row = dest + ((destY0 + py) * destStride) + (destX0 * 4);
                    for (int px = 0; px < blockW; px++)
                    {
                        int o = px * 4;
                        row[o + 0] = b;
                        row[o + 1] = g;
                        row[o + 2] = r;
                        row[o + 3] = 255;
                    }
                }
            }
        }
    }

    /// <summary>Managed-buffer overload for unit tests (BGRA32).</summary>
    public static void Fill(
        byte[] src,
        int srcStride,
        int srcWidth,
        int srcHeight,
        byte[] dest,
        int destStride,
        int x0,
        int y0,
        int x1,
        int y1,
        int blockSize,
        SampleMode mode)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dest);
        unsafe
        {
            fixed (byte* sp = src)
            {
                fixed (byte* dp = dest)
                {
                    Fill(sp, srcStride, srcWidth, srcHeight, dp, destStride, x0, y0, x1, y1, blockSize, mode);
                }
            }
        }
    }

    /// <summary>Sample one tile; exposed for tests.</summary>
    public static unsafe void SampleBlock(
        byte* src,
        int srcStride,
        int bx,
        int by,
        int blockW,
        int blockH,
        SampleMode mode,
        out byte b,
        out byte g,
        out byte r)
    {
        if (mode == SampleMode.Preview2x2)
        {
            SamplePreview2x2(src, srcStride, bx, by, blockW, blockH, out b, out g, out r);
            return;
        }

        SampleFullAverage(src, srcStride, bx, by, blockW, blockH, out b, out g, out r);
    }

    /// <summary>Managed SampleBlock for tests.</summary>
    public static void SampleBlock(
        byte[] src,
        int srcStride,
        int bx,
        int by,
        int blockW,
        int blockH,
        SampleMode mode,
        out byte b,
        out byte g,
        out byte r)
    {
        ArgumentNullException.ThrowIfNull(src);
        unsafe
        {
            fixed (byte* p = src)
            {
                SampleBlock(p, srcStride, bx, by, blockW, blockH, mode, out b, out g, out r);
            }
        }
    }

    private static unsafe void SampleFullAverage(
        byte* src,
        int srcStride,
        int bx,
        int by,
        int blockW,
        int blockH,
        out byte b,
        out byte g,
        out byte r)
    {
        long sumB = 0, sumG = 0, sumR = 0, count = 0;
        int bxEnd = bx + blockW;
        int byEnd = by + blockH;
        for (int py = by; py < byEnd; py++)
        {
            byte* row = src + (py * srcStride);
            for (int px = bx; px < bxEnd; px++)
            {
                int o = px * 4;
                sumB += row[o + 0];
                sumG += row[o + 1];
                sumR += row[o + 2];
                count++;
            }
        }

        if (count == 0)
        {
            b = g = r = 0;
            return;
        }

        b = (byte)(sumB / count);
        g = (byte)(sumG / count);
        r = (byte)(sumR / count);
    }

    /// <summary>
    /// Average the 1–4 pixels forming a 2×2 neighborhood clamped inside the tile,
    /// centered on the tile (matches "center / 2×2 sample" preview).
    /// </summary>
    private static unsafe void SamplePreview2x2(
        byte* src,
        int srcStride,
        int bx,
        int by,
        int blockW,
        int blockH,
        out byte b,
        out byte g,
        out byte r)
    {
        int cx = bx + (blockW / 2);
        int cy = by + (blockH / 2);
        int x1 = Math.Min(cx + 1, bx + blockW - 1);
        int y1 = Math.Min(cy + 1, by + blockH - 1);
        int x0 = Math.Min(cx, x1);
        int y0 = Math.Min(cy, y1);

        long sumB = 0, sumG = 0, sumR = 0, count = 0;
        for (int py = y0; py <= y1; py++)
        {
            byte* row = src + (py * srcStride);
            for (int px = x0; px <= x1; px++)
            {
                int o = px * 4;
                sumB += row[o + 0];
                sumG += row[o + 1];
                sumR += row[o + 2];
                count++;
            }
        }

        if (count == 0)
        {
            b = g = r = 0;
            return;
        }

        b = (byte)(sumB / count);
        g = (byte)(sumG / count);
        r = (byte)(sumR / count);
    }
}
