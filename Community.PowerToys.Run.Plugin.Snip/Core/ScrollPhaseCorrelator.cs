// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Adapted from LdotJdot/TDS (MIT) ScreenShot scroll phase correlator for WPF/Snip.
#pragma warning disable SA1117, SA1214, SA1316, SA1407, SA1503, SA1508, SA1512, SA1513, SA1515, SA1516, SA1518, SA1519

using System;
using System.Buffers;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// 2D 相位相关（FFT）估计纵向 dh。仓库内 radix-2 FFT（NoScaling）+ 可分离 2D 变换。
/// 输入为水平边缘强度 + 零均值归一化，减弱纯渐变误峰。
/// </summary>
internal static class ScrollPhaseCorrelator
{
    private const double MinPeakResponse = 0.35;
    private const int MaxHorizontalPeakPx = 2;

    /// <summary>
    /// FFT 单边网格上限（3000+ px 选区在 step=2 下仍会得到 4096 点列变换）。
    /// dh 精度以 step 为粒度，后续 hint 窗口 + SAD 精修会吸收该量化，无需更大网格。
    /// </summary>
    private const int MaxFftEdge = 512;

    internal readonly struct PhaseEstimate
    {
        public int Dh { get; init; }
        public double Peak { get; init; }
        public bool Success { get; init; }
    }

    /// <summary>
    /// prev 行 dh.. 应对齐 next 行 0..（与 ScrollStitcher 的 dh 语义一致）。
    /// </summary>
    internal static unsafe PhaseEstimate EstimateVerticalShift(
        byte* prevBase, int prevStride, int prevH,
        byte* nextBase, int nextStride, int nextH,
        int width, int minDh, int maxDh, int scrollHintPx)
    {
        int h = Math.Min(prevH, nextH);
        if (h <= 0 || width <= 0 || maxDh < minDh)
            return default;

        int step = width > 320 || h > 520 ? 2 : 1;
        // Raise the decimation step until the FFT grid fits under MaxFftEdge,
        // keeping huge selections from paying multi-megapoint transforms.
        while ((Math.Max(width, 8) + step - 1) / step > MaxFftEdge
               || (Math.Max(h, 8) + step - 1) / step > MaxFftEdge)
        {
            step *= 2;
        }

        int sw = (width + step - 1) / step;
        int sh = (h + step - 1) / step;
        if (sw < 8 || sh < 8)
            return default;

        int fftRows = NextPow2(sh * 2);
        int fftCols = NextPow2(sw * 2);
        int n = fftRows * fftCols;

        double[] a = ArrayPool<double>.Shared.Rent(n);
        double[] b = ArrayPool<double>.Shared.Rent(n);
        ComplexF[] surface = ArrayPool<ComplexF>.Shared.Rent(n);
        try
        {
            a.AsSpan(0, n).Clear();
            b.AsSpan(0, n).Clear();
            FillHorizontalEdge(prevBase, prevStride, prevH, width, step, a, fftCols, sh, sw);
            FillHorizontalEdge(nextBase, nextStride, nextH, width, step, b, fftCols, sh, sw);
            NormalizeZeroMeanInPlace(a, sh, sw, fftCols);
            NormalizeZeroMeanInPlace(b, sh, sw, fftCols);
            ApplyHannInPlace(a, sh, sw, fftRows, fftCols);
            ApplyHannInPlace(b, sh, sw, fftRows, fftCols);

            PhaseCorrelateSurfaceInto(a.AsSpan(0, n), b.AsSpan(0, n), surface.AsSpan(0, n), fftRows, fftCols);

            int minRow = Math.Max(1, (minDh + step - 1) / step);
            int maxRow = Math.Min(sh - 1, maxDh / step);
            if (scrollHintPx > 0)
            {
                int slack = Math.Max(12, (int)(scrollHintPx * 0.35 / step));
                int hintRow = scrollHintPx / step;
                minRow = Math.Max(minRow, hintRow - slack);
                maxRow = Math.Min(maxRow, hintRow + slack);
            }
            if (minRow > maxRow)
                return default;

            FindPeak(surface.AsSpan(0, n), fftRows, fftCols, minRow, maxRow, out int peakRow, out int peakCol, out double peakVal);
            if (peakVal < MinPeakResponse || Math.Abs(peakCol) > MaxHorizontalPeakPx)
                return default;

            int dh = peakRow * step;
            if (dh < minDh || dh > maxDh)
                return default;

            return new PhaseEstimate { Dh = dh, Peak = peakVal, Success = true };
        }
        finally
        {
            ArrayPool<ComplexF>.Shared.Return(surface);
            ArrayPool<double>.Shared.Return(b);
            ArrayPool<double>.Shared.Return(a);
        }
    }

    private static void PhaseCorrelateSurfaceInto(
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> b,
        Span<ComplexF> cross,
        int fftRows,
        int fftCols)
    {
        int n = fftRows * fftCols;
        ComplexF[] faArr = ArrayPool<ComplexF>.Shared.Rent(n);
        ComplexF[] fbArr = ArrayPool<ComplexF>.Shared.Rent(n);
        try
        {
            Span<ComplexF> fa = faArr.AsSpan(0, n);
            Span<ComplexF> fb = fbArr.AsSpan(0, n);
            for (int i = 0; i < n; i++)
            {
                fa[i] = new ComplexF((float)a[i], 0);
                fb[i] = new ComplexF((float)b[i], 0);
            }

            FourierTransform2D(fa, fftRows, fftCols, inverse: false);
            FourierTransform2D(fb, fftRows, fftCols, inverse: false);

            for (int i = 0; i < n; i++)
            {
                var c = fa[i] * fb[i].Conjugate;
                float mag = c.Magnitude;
                cross[i] = mag > 1e-6f ? c / mag : ComplexF.Zero;
            }

            FourierTransform2D(cross, fftRows, fftCols, inverse: true);
        }
        finally
        {
            ArrayPool<ComplexF>.Shared.Return(fbArr);
            ArrayPool<ComplexF>.Shared.Return(faArr);
        }
    }

    private static void FourierTransform2D(Span<ComplexF> data, int rows, int cols, bool inverse)
    {
        for (int r = 0; r < rows; r++)
        {
            Span<ComplexF> rowSpan = data.Slice(r * cols, cols);
            if (inverse)
                Radix2Fft.Inverse(rowSpan);
            else
                Radix2Fft.Forward(rowSpan);
        }

        ComplexF[] colArr = ArrayPool<ComplexF>.Shared.Rent(rows);
        try
        {
            Span<ComplexF> colBuf = colArr.AsSpan(0, rows);
            for (int c = 0; c < cols; c++)
            {
                for (int r = 0; r < rows; r++)
                    colBuf[r] = data[r * cols + c];
                if (inverse)
                    Radix2Fft.Inverse(colBuf);
                else
                    Radix2Fft.Forward(colBuf);
                for (int r = 0; r < rows; r++)
                    data[r * cols + c] = colBuf[r];
            }
        }
        finally
        {
            ArrayPool<ComplexF>.Shared.Return(colArr);
        }
    }

    private static void FindPeak(
        ReadOnlySpan<ComplexF> surface, int fftRows, int fftCols,
        int minRow, int maxRow,
        out int peakRow, out int peakCol, out double peakVal)
    {
        peakRow = 0;
        peakCol = 0;
        peakVal = double.MinValue;

        for (int row = minRow; row <= maxRow; row++)
        {
            for (int col = 0; col <= MaxHorizontalPeakPx; col++)
            {
                double v = surface[row * fftCols + col].Real;
                if (v > peakVal)
                {
                    peakVal = v;
                    peakRow = row;
                    peakCol = col;
                }
            }
        }

        if (peakVal == double.MinValue)
        {
            for (int row = fftRows - maxRow; row <= fftRows - minRow; row++)
            {
                if (row < 0) continue;
                int dhRow = fftRows - row;
                for (int col = 0; col <= MaxHorizontalPeakPx; col++)
                {
                    double v = surface[row * fftCols + col].Real;
                    if (v > peakVal)
                    {
                        peakVal = v;
                        peakRow = dhRow;
                        peakCol = col;
                    }
                }
            }
        }
    }

    private static unsafe void FillHorizontalEdge(
        byte* srcBase, int srcStride, int srcH, int width, int step,
        double[] dst, int dstStride, int dstH, int dstW)
    {
        for (int y = 0; y < dstH; y++)
        {
            int sy = Math.Min(y * step, srcH - 1);
            byte* row = srcBase + (long)sy * srcStride;
            for (int x = 0; x < dstW; x++)
            {
                int sx0 = Math.Min(x * step, width - 1);
                int sx1 = Math.Min(sx0 + step, width - 1);
                int o0 = sx0 * 4;
                int o1 = sx1 * 4;
                int l0 = (299 * row[o0 + 2] + 587 * row[o0 + 1] + 114 * row[o0]) / 1000;
                int l1 = (299 * row[o1 + 2] + 587 * row[o1 + 1] + 114 * row[o1]) / 1000;
                dst[y * dstStride + x] = Math.Abs(l1 - l0);
            }
        }
    }

    private static void NormalizeZeroMeanInPlace(double[] buf, int activeH, int activeW, int stride)
    {
        double sum = 0;
        int count = activeH * activeW;
        for (int y = 0; y < activeH; y++)
        for (int x = 0; x < activeW; x++)
            sum += buf[y * stride + x];
        double mean = count > 0 ? sum / count : 0;
        double sq = 0;
        for (int y = 0; y < activeH; y++)
        for (int x = 0; x < activeW; x++)
        {
            buf[y * stride + x] -= mean;
            sq += buf[y * stride + x] * buf[y * stride + x];
        }
        double norm = Math.Sqrt(sq / Math.Max(1, count));
        if (norm < 1e-6) return;
        for (int y = 0; y < activeH; y++)
        for (int x = 0; x < activeW; x++)
            buf[y * stride + x] /= norm;
    }

    private static void ApplyHannInPlace(
        double[] buf, int activeH, int activeW, int fftRows, int fftCols)
    {
        for (int y = 0; y < activeH; y++)
        {
            double wy = activeH <= 1 ? 1.0 : 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * y / (activeH - 1)));
            for (int x = 0; x < activeW; x++)
            {
                double wx = activeW <= 1 ? 1.0 : 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * x / (activeW - 1)));
                buf[y * fftCols + x] *= wx * wy;
            }
        }
    }

    private static int NextPow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }
}
