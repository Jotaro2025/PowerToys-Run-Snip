// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

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
                uint packed = (uint)(b | (g << 8) | (r << 16) | 0xFF000000);
                for (int py = 0; py < blockH; py++)
                {
                    var row = new Span<uint>(dest + ((destY0 + py) * destStride) + (destX0 * 4), blockW);
                    row.Fill(packed);
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

    // Channel masks in the widened uint domain: after two Widen steps each
    // Vector<byte> (N BGRA pixels) becomes Vector<uint> lanes holding single bytes
    // in [B,G,R,A] order — lane index ≡ byte offset (mod 4). Built per
    // Vector<uint>.Count so they cover AVX-512's 16 lanes as well as SSE/AVX2's 8.
    private static readonly Vector<uint> MaskB = BuildChannelMask(0);
    private static readonly Vector<uint> MaskG = BuildChannelMask(1);
    private static readonly Vector<uint> MaskR = BuildChannelMask(2);

    private static Vector<uint> BuildChannelMask(int channel)
    {
        var lanes = new uint[Vector<uint>.Count];
        for (int i = channel; i < lanes.Length; i += 4)
        {
            lanes[i] = 0xFFFFFFFF;
        }

        return new Vector<uint>(lanes);
    }

    private static unsafe Vector<byte> LoadVector(byte* p) => *(Vector<byte>*)p;

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
        int bxEnd = bx + blockW;
        int byEnd = by + blockH;
        long sumB = 0, sumG = 0, sumR = 0, count = 0;
        int rowPixels = blockW;
        int vecPixels = Vector<byte>.Count / 4;

        if (Vector.IsHardwareAccelerated && rowPixels >= vecPixels)
        {
            Vector<uint> accB = default, accG = default, accR = default;
            long vecCount = 0, tailCount = 0;
            for (int py = by; py < byEnd; py++)
            {
                byte* row = src + (py * srcStride) + (bx * 4);
                int px = 0;
                for (; px + vecPixels <= rowPixels; px += vecPixels)
                {
                    var vb = LoadVector(row + (px * 4));
                    Vector.Widen(vb, out Vector<ushort> lo16, out Vector<ushort> hi16);
                    Vector.Widen(lo16, out Vector<uint> q0, out Vector<uint> q1);
                    Vector.Widen(hi16, out Vector<uint> q2, out Vector<uint> q3);
                    accB += (q0 & MaskB) + (q1 & MaskB) + (q2 & MaskB) + (q3 & MaskB);
                    accG += (q0 & MaskG) + (q1 & MaskG) + (q2 & MaskG) + (q3 & MaskG);
                    accR += (q0 & MaskR) + (q1 & MaskR) + (q2 & MaskR) + (q3 & MaskR);
                }

                vecCount += px;

                // Scalar tail for the last <vector-width pixels of the row
                // (also the whole row when it is narrower than one vector).
                for (; px < rowPixels; px++)
                {
                    int o = px * 4;
                    sumB += row[o + 0];
                    sumG += row[o + 1];
                    sumR += row[o + 2];
                    tailCount++;
                }
            }

            count = vecCount + tailCount;

            // Lane-reduce the accumulators (8 lanes each; headroom is ample —
            // 255 × pixels-per-block fits uint for any block ≤ 16M pixels).
            for (int i = 0; i < Vector<uint>.Count; i++)
            {
                sumB += accB[i];
                sumG += accG[i];
                sumR += accR[i];
            }
        }
        else
        {
            for (int py = by; py < byEnd; py++)
            {
                byte* row = src + (py * srcStride);
                for (int px = bx; px < bxEnd; px++)
                {
                    int o = px * 4;
                    sumB += row[o + 0];
                    sumG += row[o + 1];
                    sumR += row[o + 2];
                }
            }

            count = (long)blockW * blockH;
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

    /// <summary>
    /// Fills a BGRA32 destination covering [<paramref name="x0"/>,<paramref name="x1"/>) × [<paramref name="y0"/>,<paramref name="y1"/>)
    /// with a two-pass separable box/Gaussian approximation blur using <see cref="ArrayPool{T}.Shared"/>.
    /// </summary>
    public static unsafe void FillGaussianBlur(
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
        int radius)
    {
        if (src == null || dest == null)
        {
            throw new ArgumentNullException(src == null ? nameof(src) : nameof(dest));
        }

        x0 = Math.Clamp(x0, 0, srcWidth);
        y0 = Math.Clamp(y0, 0, srcHeight);
        x1 = Math.Clamp(x1, 0, srcWidth);
        y1 = Math.Clamp(y1, 0, srcHeight);
        int w = x1 - x0;
        int h = y1 - y0;
        if (w <= 0 || h <= 0 || srcStride < srcWidth * 4 || destStride < w * 4)
        {
            return;
        }

        int r = Math.Clamp(radius, 1, 64);
        int tempStride = w * 4;
        int tempBytes = tempStride * h;
        byte[] rented = ArrayPool<byte>.Shared.Rent(tempBytes);

        // Per-column sliding-window sums for the vertical pass; O(1) per pixel
        // instead of re-summing (2r+1) taps: add the entering row, drop the leaving one.
        int[] sumRented = ArrayPool<int>.Shared.Rent(w * 3);
        try
        {
            fixed (byte* temp = rented)
            {
                Span<int> sums = sumRented.AsSpan(0, w * 3);
                Span<int> sumB = sums.Slice(0, w);
                Span<int> sumG = sums.Slice(w, w);
                Span<int> sumR = sums.Slice(w * 2, w);

                // Pass 1: horizontal sliding-window box blur from src[x0..x1, y0..y1]
                // into temp[0..w, 0..h].
                for (int y = 0; y < h; y++)
                {
                    byte* sRow = src + ((y0 + y) * srcStride);
                    byte* tRow = temp + (y * tempStride);
                    int sx0 = 0;
                    int sx1 = Math.Min(r, w - 1);
                    int count = sx1 - sx0 + 1;
                    int b = 0, g = 0, rr = 0;
                    for (int sx = sx0; sx <= sx1; sx++)
                    {
                        int o = (x0 + sx) * 4;
                        b += sRow[o + 0];
                        g += sRow[o + 1];
                        rr += sRow[o + 2];
                    }

                    for (int x = 0; x < w; x++)
                    {
                        int dOff = x * 4;
                        tRow[dOff + 0] = (byte)(b / count);
                        tRow[dOff + 1] = (byte)(g / count);
                        tRow[dOff + 2] = (byte)(rr / count);
                        tRow[dOff + 3] = 255;

                        // The window grows at the left edge, slides in the middle,
                        // and shrinks after the right edge — no clamped re-add.
                        if (x + r + 1 < w)
                        {
                            int oAdd = (x0 + x + r + 1) * 4;
                            b += sRow[oAdd + 0];
                            g += sRow[oAdd + 1];
                            rr += sRow[oAdd + 2];
                            count++;
                        }

                        if (x >= r)
                        {
                            int oRemove = (x0 + (x - r)) * 4;
                            b -= sRow[oRemove + 0];
                            g -= sRow[oRemove + 1];
                            rr -= sRow[oRemove + 2];
                            count--;
                        }
                    }
                }

                // Pass 2: vertical sliding-window box blur from temp into dest,
                // keeping per-column sums so each row update is O(w) not O(w*r).
                int countV = Math.Min(r, h - 1) + 1;
                for (int x = 0; x < w; x++)
                {
                    int xOff = x * 4;
                    int b = 0, g = 0, rr = 0;
                    for (int sy = 0; sy <= Math.Min(r, h - 1); sy++)
                    {
                        byte* tRow = temp + (sy * tempStride) + xOff;
                        b += tRow[0];
                        g += tRow[1];
                        rr += tRow[2];
                    }

                    sumB[x] = b;
                    sumG[x] = g;
                    sumR[x] = rr;
                }

                for (int y = 0; y < h; y++)
                {
                    byte* dRow = dest + (y * destStride);
                    for (int x = 0; x < w; x++)
                    {
                        int xOff = x * 4;
                        dRow[xOff + 0] = (byte)(sumB[x] / countV);
                        dRow[xOff + 1] = (byte)(sumG[x] / countV);
                        dRow[xOff + 2] = (byte)(sumR[x] / countV);
                        dRow[xOff + 3] = 255;
                    }

                    // Slide the window down: add row y+r+1, drop row y-r.
                    int addY = y + r + 1;
                    if (addY < h)
                    {
                        byte* aRow = temp + (addY * tempStride);
                        for (int x = 0; x < w; x++)
                        {
                            int xOff = x * 4;
                            sumB[x] += aRow[xOff + 0];
                            sumG[x] += aRow[xOff + 1];
                            sumR[x] += aRow[xOff + 2];
                        }

                        countV++;
                    }

                    int removeY = y - r;
                    if (removeY >= 0)
                    {
                        byte* dRow2 = temp + (removeY * tempStride);
                        for (int x = 0; x < w; x++)
                        {
                            int xOff = x * 4;
                            sumB[x] -= dRow2[xOff + 0];
                            sumG[x] -= dRow2[xOff + 1];
                            sumR[x] -= dRow2[xOff + 2];
                        }

                        countV--;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
            ArrayPool<int>.Shared.Return(sumRented);
        }
    }

    /// <summary>Managed-buffer overload of <see cref="FillGaussianBlur(byte*,int,int,int,byte*,int,int,int,int,int,int)"/> for unit tests.</summary>
    public static void FillGaussianBlur(
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
        int radius)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dest);
        unsafe
        {
            fixed (byte* sp = src)
            {
                fixed (byte* dp = dest)
                {
                    FillGaussianBlur(sp, srcStride, srcWidth, srcHeight, dp, destStride, x0, y0, x1, y1, radius);
                }
            }
        }
    }

    /// <summary>Fills a BGRA32 destination rectangle with a solid color for redaction.</summary>
    public static unsafe void FillSolid(
        byte* dest,
        int destStride,
        int width,
        int height,
        byte b,
        byte g,
        byte r,
        byte a = 255)
    {
        if (dest == null)
        {
            throw new ArgumentNullException(nameof(dest));
        }

        if (width <= 0 || height <= 0 || destStride < width * 4)
        {
            return;
        }

        uint packed = (uint)(b | (g << 8) | (r << 16) | (a << 24));
        for (int y = 0; y < height; y++)
        {
            var row = new Span<uint>(dest + (y * destStride), width);
            row.Fill(packed);
        }
    }

    /// <summary>Managed-buffer overload of <see cref="FillSolid(byte*,int,int,int,byte,byte,byte,byte)"/> for unit tests.</summary>
    public static void FillSolid(
        byte[] dest,
        int destStride,
        int width,
        int height,
        byte b,
        byte g,
        byte r,
        byte a = 255)
    {
        ArgumentNullException.ThrowIfNull(dest);
        unsafe
        {
            fixed (byte* dp = dest)
            {
                FillSolid(dp, destStride, width, height, b, g, r, a);
            }
        }
    }
}
