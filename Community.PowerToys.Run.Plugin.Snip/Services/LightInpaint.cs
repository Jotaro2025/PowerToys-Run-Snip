// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Pure managed Telea-inspired inpaint for small OCR erase masks (no OpenCV).
/// Onion-peels the unknown region from the known border, estimating each pixel
/// as a distance-weighted blend of nearby known samples (with a cheap gradient term).
/// Uses a region-local mask and a frontier queue (no full-image <c>bool[W*H]</c>,
/// no rescanning the whole rect every band).
/// </summary>
public static class LightInpaint
{
    /// <summary>Neighborhood radius in pixels for Telea-like weights.</summary>
    public const int DefaultRadius = 5;

    /// <summary>Skip inpaint when either side of the region is below this (use median fill).</summary>
    public const int MinSide = 4;

    /// <summary>Skip inpaint when the region has fewer interior pixels than this.</summary>
    public const int MinInteriorPixels = 8;

    /// <summary>Safety cap — huge masks fall back to median (OCR boxes are small).</summary>
    public const int MaxInteriorPixels = 120_000;

    private const float Eps = 0.1f;

    /// <summary>
    /// Length of the region-local unknown mask from the last <see cref="TryInpaintRect"/>
    /// that allocated one (0 if the call returned early). For unit tests.
    /// </summary>
    internal static int LastRegionMaskLength { get; private set; }

    /// <summary>Expected region-local mask length for a rect of size rw×rh.</summary>
    internal static int RegionMaskLength(int regionWidth, int regionHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(regionWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(regionHeight);
        return checked(regionWidth * regionHeight);
    }

    /// <summary>
    /// Inpaint the half-open axis-aligned region [<paramref name="x0"/>,<paramref name="x1"/>) ×
    /// [<paramref name="y0"/>,<paramref name="y1"/>) in a BGRA32 buffer.
    /// Pixels outside the region stay known; interior is reconstructed from the border.
    /// </summary>
    /// <returns><c>false</c> when the region is too small/large or could not be filled.</returns>
    public static bool TryInpaintRect(
        byte[] pixels,
        int stride,
        int width,
        int height,
        int x0,
        int y0,
        int x1,
        int y1,
        int radius = DefaultRadius)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        LastRegionMaskLength = 0;
        if (width <= 0 || height <= 0 || stride < width * 4)
        {
            return false;
        }

        x0 = Math.Clamp(x0, 0, width);
        y0 = Math.Clamp(y0, 0, height);
        x1 = Math.Clamp(x1, 0, width);
        y1 = Math.Clamp(y1, 0, height);
        int rw = x1 - x0;
        int rh = y1 - y0;
        if (rw < MinSide || rh < MinSide)
        {
            return false;
        }

        int interior = rw * rh;
        if (interior < MinInteriorPixels || interior > MaxInteriorPixels)
        {
            return false;
        }

        if (radius < 1)
        {
            radius = 1;
        }

        // Region-local mask only (not bool[width*height]).
        var unknown = new bool[interior];
        LastRegionMaskLength = unknown.Length;
        for (int i = 0; i < unknown.Length; i++)
        {
            unknown[i] = true;
        }

        int unknownCount = interior;

        // Onion-peel frontier: seed from the rect perimeter, then expand via neighbors
        // of filled pixels (no full-region rescan each band).
        var frontier = new List<int>(Math.Min(interior, 4096));
        var nextFrontier = new List<int>(Math.Min(interior, 4096));
        var queued = new bool[interior];
        SeedFrontier(unknown, queued, frontier, rw, rh, x0, y0, width, height);

        int guard = unknownCount + 8;
        while (unknownCount > 0 && guard-- > 0)
        {
            if (frontier.Count == 0)
            {
                // Isolated islands — cannot propagate; caller should median-fill.
                return false;
            }

            // Estimate all band pixels from the *current* known set, then commit,
            // then expand the frontier (so same-band neighbors are not re-queued).
            var estimates = new byte[frontier.Count * 4];
            for (int i = 0; i < frontier.Count; i++)
            {
                int li = frontier[i];
                int lx = li % rw;
                int ly = li / rw;
                int x = x0 + lx;
                int y = y0 + ly;
                EstimatePixel(
                    pixels,
                    unknown,
                    stride,
                    width,
                    height,
                    x0,
                    y0,
                    rw,
                    rh,
                    x,
                    y,
                    radius,
                    estimates,
                    i * 4);
            }

            for (int i = 0; i < frontier.Count; i++)
            {
                int li = frontier[i];
                int lx = li % rw;
                int ly = li / rw;
                int x = x0 + lx;
                int y = y0 + ly;
                int pi = (y * stride) + (x * 4);
                int ei = i * 4;
                pixels[pi] = estimates[ei];
                pixels[pi + 1] = estimates[ei + 1];
                pixels[pi + 2] = estimates[ei + 2];
                pixels[pi + 3] = 255;
                unknown[li] = false;
                queued[li] = false;
                unknownCount--;
            }

            nextFrontier.Clear();
            for (int i = 0; i < frontier.Count; i++)
            {
                int li = frontier[i];
                int lx = li % rw;
                int ly = li / rw;
                EnqueueUnknownNeighbors(
                    unknown,
                    queued,
                    nextFrontier,
                    rw,
                    rh,
                    lx,
                    ly);
            }

            // Swap bands.
            var swap = frontier;
            frontier = nextFrontier;
            nextFrontier = swap;
        }

        return unknownCount == 0;
    }

    /// <summary>Fill a rect with a solid BGRA color (median fallback).</summary>
    public static void FillRectSolid(
        byte[] pixels,
        int stride,
        int width,
        int height,
        int x0,
        int y0,
        int x1,
        int y1,
        byte b,
        byte g,
        byte r,
        byte a = 255)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        x0 = Math.Clamp(x0, 0, width);
        y0 = Math.Clamp(y0, 0, height);
        x1 = Math.Clamp(x1, 0, width);
        y1 = Math.Clamp(y1, 0, height);
        for (int y = y0; y < y1; y++)
        {
            int row = y * stride;
            for (int x = x0; x < x1; x++)
            {
                int i = row + (x * 4);
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
        }
    }

    /// <summary>
    /// Mean absolute error of BGR channels between two buffers over a rect (heuristic metric).
    /// </summary>
    public static double MeanAbsErrorBgr(
        byte[] a,
        byte[] b,
        int stride,
        int x0,
        int y0,
        int x1,
        int y1)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        long sum = 0;
        long count = 0;
        for (int y = y0; y < y1; y++)
        {
            int row = y * stride;
            for (int x = x0; x < x1; x++)
            {
                int i = row + (x * 4);
                sum += Math.Abs(a[i] - b[i]);
                sum += Math.Abs(a[i + 1] - b[i + 1]);
                sum += Math.Abs(a[i + 2] - b[i + 2]);
                count += 3;
            }
        }

        return count == 0 ? 0 : (double)sum / count;
    }

    /// <summary>
    /// Sum of absolute 4-neighbor Laplacian on luminance inside a rect (high-frequency energy).
    /// </summary>
    public static double HighFreqEnergy(
        byte[] pixels,
        int stride,
        int width,
        int height,
        int x0,
        int y0,
        int x1,
        int y1)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        x0 = Math.Max(1, x0);
        y0 = Math.Max(1, y0);
        x1 = Math.Min(width - 1, x1);
        y1 = Math.Min(height - 1, y1);
        double sum = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                float c = Luma(pixels, stride, x, y);
                float lap = Math.Abs(
                    (4 * c)
                    - Luma(pixels, stride, x - 1, y)
                    - Luma(pixels, stride, x + 1, y)
                    - Luma(pixels, stride, x, y - 1)
                    - Luma(pixels, stride, x, y + 1));
                sum += lap;
            }
        }

        return sum;
    }

    /// <summary>
    /// Seed the first peel band: region perimeter pixels that already touch known
    /// (outside the OCR box or image-known neighbors).
    /// </summary>
    private static void SeedFrontier(
        bool[] unknown,
        bool[] queued,
        List<int> frontier,
        int rw,
        int rh,
        int x0,
        int y0,
        int width,
        int height)
    {
        void TryAdd(int lx, int ly)
        {
            int li = (ly * rw) + lx;
            if (queued[li] || !unknown[li])
            {
                return;
            }

            int x = x0 + lx;
            int y = y0 + ly;
            if (!HasKnownNeighbor(unknown, rw, rh, x0, y0, width, height, x, y))
            {
                return;
            }

            queued[li] = true;
            frontier.Add(li);
        }

        for (int lx = 0; lx < rw; lx++)
        {
            TryAdd(lx, 0);
            if (rh > 1)
            {
                TryAdd(lx, rh - 1);
            }
        }

        for (int ly = 1; ly < rh - 1; ly++)
        {
            TryAdd(0, ly);
            if (rw > 1)
            {
                TryAdd(rw - 1, ly);
            }
        }
    }

    private static void EnqueueUnknownNeighbors(
        bool[] unknown,
        bool[] queued,
        List<int> nextFrontier,
        int rw,
        int rh,
        int lx,
        int ly)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                int nx = lx + dx;
                int ny = ly + dy;
                if (nx < 0 || ny < 0 || nx >= rw || ny >= rh)
                {
                    continue;
                }

                int ni = (ny * rw) + nx;
                if (!unknown[ni] || queued[ni])
                {
                    continue;
                }

                // Neighbor of a just-filled pixel → touches known; ready for next band.
                queued[ni] = true;
                nextFrontier.Add(ni);
            }
        }
    }

    /// <summary>
    /// True when (x,y) still needs fill. Outside the image → treat as unknown (no sample).
    /// Outside the OCR region but inside the image → known.
    /// </summary>
    private static bool IsUnknown(
        bool[] unknown,
        int rw,
        int rh,
        int x0,
        int y0,
        int width,
        int height,
        int x,
        int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return true;
        }

        if (x < x0 || y < y0 || x >= x0 + rw || y >= y0 + rh)
        {
            return false;
        }

        return unknown[((y - y0) * rw) + (x - x0)];
    }

    private static bool HasKnownNeighbor(
        bool[] unknown,
        int rw,
        int rh,
        int x0,
        int y0,
        int width,
        int height,
        int x,
        int y)
    {
        // 4-neighbors + diagonals (same as before).
        if (x > 0 && !IsUnknown(unknown, rw, rh, x0, y0, width, height, x - 1, y))
        {
            return true;
        }

        if (x + 1 < width && !IsUnknown(unknown, rw, rh, x0, y0, width, height, x + 1, y))
        {
            return true;
        }

        if (y > 0 && !IsUnknown(unknown, rw, rh, x0, y0, width, height, x, y - 1))
        {
            return true;
        }

        if (y + 1 < height && !IsUnknown(unknown, rw, rh, x0, y0, width, height, x, y + 1))
        {
            return true;
        }

        if (x > 0 && y > 0 && !IsUnknown(unknown, rw, rh, x0, y0, width, height, x - 1, y - 1))
        {
            return true;
        }

        if (x + 1 < width && y > 0 && !IsUnknown(unknown, rw, rh, x0, y0, width, height, x + 1, y - 1))
        {
            return true;
        }

        if (x > 0 && y + 1 < height && !IsUnknown(unknown, rw, rh, x0, y0, width, height, x - 1, y + 1))
        {
            return true;
        }

        if (x + 1 < width && y + 1 < height && !IsUnknown(unknown, rw, rh, x0, y0, width, height, x + 1, y + 1))
        {
            return true;
        }

        return false;
    }

    private static void EstimatePixel(
        byte[] pixels,
        bool[] unknown,
        int stride,
        int width,
        int height,
        int x0,
        int y0,
        int rw,
        int rh,
        int x,
        int y,
        int radius,
        byte[] dest,
        int destOffset)
    {
        double sumB = 0, sumG = 0, sumR = 0, sumW = 0;
        int xMin = Math.Max(0, x - radius);
        int xMax = Math.Min(width - 1, x + radius);
        int yMin = Math.Max(0, y - radius);
        int yMax = Math.Min(height - 1, y + radius);

        for (int qy = yMin; qy <= yMax; qy++)
        {
            for (int qx = xMin; qx <= xMax; qx++)
            {
                if (IsUnknown(unknown, rw, rh, x0, y0, width, height, qx, qy))
                {
                    continue;
                }

                int dx = x - qx;
                int dy = y - qy;
                int distSq = (dx * dx) + (dy * dy);
                if (distSq == 0 || distSq > radius * radius)
                {
                    continue;
                }

                double dist = Math.Sqrt(distSq);

                // Directional / distance weight (Telea-inspired dst term).
                double w = 1.0 / (dist + Eps);

                // Cheap gradient at q from known finite differences.
                float gxB = 0, gyB = 0, gxG = 0, gyG = 0, gxR = 0, gyR = 0;
                SampleGrad(pixels, unknown, stride, width, height, x0, y0, rw, rh, qx, qy, ref gxB, ref gyB, 0);
                SampleGrad(pixels, unknown, stride, width, height, x0, y0, rw, rh, qx, qy, ref gxG, ref gyG, 1);
                SampleGrad(pixels, unknown, stride, width, height, x0, y0, rw, rh, qx, qy, ref gxR, ref gyR, 2);

                int qi = (qy * stride) + (qx * 4);
                double b = pixels[qi] + (gxB * dx) + (gyB * dy);
                double g = pixels[qi + 1] + (gxG * dx) + (gyG * dy);
                double r = pixels[qi + 2] + (gxR * dx) + (gyR * dy);

                sumB += w * b;
                sumG += w * g;
                sumR += w * r;
                sumW += w;
            }
        }

        if (sumW <= 0)
        {
            dest[destOffset] = 0;
            dest[destOffset + 1] = 0;
            dest[destOffset + 2] = 0;
            dest[destOffset + 3] = 255;
            return;
        }

        dest[destOffset] = ClampByte(sumB / sumW);
        dest[destOffset + 1] = ClampByte(sumG / sumW);
        dest[destOffset + 2] = ClampByte(sumR / sumW);
        dest[destOffset + 3] = 255;
    }

    private static void SampleGrad(
        byte[] pixels,
        bool[] unknown,
        int stride,
        int width,
        int height,
        int x0,
        int y0,
        int rw,
        int rh,
        int x,
        int y,
        ref float gx,
        ref float gy,
        int channel)
    {
        float left = ChannelOrNaN(pixels, unknown, stride, width, height, x0, y0, rw, rh, x - 1, y, channel);
        float right = ChannelOrNaN(pixels, unknown, stride, width, height, x0, y0, rw, rh, x + 1, y, channel);
        float up = ChannelOrNaN(pixels, unknown, stride, width, height, x0, y0, rw, rh, x, y - 1, channel);
        float down = ChannelOrNaN(pixels, unknown, stride, width, height, x0, y0, rw, rh, x, y + 1, channel);
        float center = pixels[(y * stride) + (x * 4) + channel];

        if (!float.IsNaN(left) && !float.IsNaN(right))
        {
            gx = (right - left) * 0.5f;
        }
        else if (!float.IsNaN(right))
        {
            gx = right - center;
        }
        else if (!float.IsNaN(left))
        {
            gx = center - left;
        }
        else
        {
            gx = 0;
        }

        if (!float.IsNaN(up) && !float.IsNaN(down))
        {
            gy = (down - up) * 0.5f;
        }
        else if (!float.IsNaN(down))
        {
            gy = down - center;
        }
        else if (!float.IsNaN(up))
        {
            gy = center - up;
        }
        else
        {
            gy = 0;
        }
    }

    private static float ChannelOrNaN(
        byte[] pixels,
        bool[] unknown,
        int stride,
        int width,
        int height,
        int x0,
        int y0,
        int rw,
        int rh,
        int x,
        int y,
        int channel)
    {
        if (x < 0 || y < 0 || x >= width || y >= height
            || IsUnknown(unknown, rw, rh, x0, y0, width, height, x, y))
        {
            return float.NaN;
        }

        return pixels[(y * stride) + (x * 4) + channel];
    }

    private static float Luma(byte[] pixels, int stride, int x, int y)
    {
        int i = (y * stride) + (x * 4);
        return (0.0722f * pixels[i]) + (0.7152f * pixels[i + 1]) + (0.2126f * pixels[i + 2]);
    }

    private static byte ClampByte(double v)
        => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
