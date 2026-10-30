// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Least-squares plane <c>C(x, y) = a + b·u + c·v</c> (u/v centered on the sample
/// centroid) fitted to the background ring around a text box. Flat UI surfaces and
/// linear gradients fit with a tiny residual, so the erased glyph pixels can be
/// reconstructed exactly instead of smeared by an inpaint. Photo/texture backgrounds
/// leave a high residual and the fit is rejected.
/// </summary>
internal sealed class BackgroundPlane
{
    /// <summary>Minimum samples needed before a plane is trusted.</summary>
    internal const int MinSamples = 12;

    /// <summary>Largest per-channel RMS residual that still counts as flat/gradient.</summary>
    internal const double MaxRms = 6.0;

    private readonly double _cx;
    private readonly double _cy;
    private readonly double _aB;
    private readonly double _aG;
    private readonly double _aR;
    private readonly double _bB;
    private readonly double _bG;
    private readonly double _bR;
    private readonly double _cB;
    private readonly double _cG;
    private readonly double _cR;

    private BackgroundPlane(
        double cx,
        double cy,
        double[] b,
        double[] g,
        double[] r,
        int sampleCount,
        double rms)
    {
        _cx = cx;
        _cy = cy;
        _aB = b[0];
        _bB = b[1];
        _cB = b[2];
        _aG = g[0];
        _bG = g[1];
        _cG = g[2];
        _aR = r[0];
        _bR = r[1];
        _cR = r[2];
        SampleCount = sampleCount;
        Rms = rms;
    }

    public int SampleCount { get; }

    /// <summary>Worst per-channel RMS residual over the samples kept by the fit.</summary>
    public double Rms { get; }

    /// <summary>
    /// Fits a plane to <paramref name="samples"/>; outliers are dropped once and the
    /// remaining samples refit. Returns null when there are too few samples, the
    /// samples are degenerate (collinear), or the residual is too high.
    /// </summary>
    public static BackgroundPlane Fit(IReadOnlyList<BackgroundSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < MinSamples)
        {
            return null;
        }

        double cx = 0;
        double cy = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            cx += samples[i].X;
            cy += samples[i].Y;
        }

        cx /= samples.Count;
        cy /= samples.Count;

        var used = new bool[samples.Count];
        Array.Fill(used, true);
        int usedCount = samples.Count;
        double[] coefficients = null;
        double rms = double.MaxValue;

        for (int pass = 0; pass < 2; pass++)
        {
            if (!TrySolve(samples, used, usedCount, cx, cy, out coefficients, out double[] residuals))
            {
                return null;
            }

            double sumSq = 0;
            for (int i = 0; i < residuals.Length; i++)
            {
                sumSq += residuals[i] * residuals[i];
            }

            rms = Math.Sqrt(sumSq / Math.Max(1, usedCount));

            // Drop gross outliers once (a neighbouring glyph or icon in the ring),
            // then refit; the second pass result is what we trust.
            int removed = 0;
            double limit = Math.Max(8.0, rms * 3.0);
            for (int i = 0; i < samples.Count; i++)
            {
                if (used[i] && residuals[i] > limit)
                {
                    used[i] = false;
                    usedCount--;
                    removed++;
                }
            }

            if (removed == 0 || usedCount < MinSamples)
            {
                break;
            }
        }

        if (coefficients == null || rms > MaxRms || usedCount < MinSamples)
        {
            return null;
        }

        return new BackgroundPlane(
            cx,
            cy,
            coefficients[..3],
            coefficients[3..6],
            coefficients[6..9],
            usedCount,
            rms);
    }

    /// <summary>Predicted background color at image pixel (x, y).</summary>
    public Color At(int x, int y)
    {
        double u = x - _cx;
        double v = y - _cy;
        return Color.FromRgb(
            Clamp(_aR + (_bR * u) + (_cR * v)),
            Clamp(_aG + (_bG * u) + (_cG * v)),
            Clamp(_aB + (_bB * u) + (_cB * v)));
    }

    /// <summary>
    /// Max-channel absolute difference between the pixel and the predicted background:
    /// 0 means the pixel is exactly explained by the plane (flat/gradient background).
    /// </summary>
    public int Distance(byte b, byte g, byte r, int x, int y)
    {
        double u = x - _cx;
        double v = y - _cy;
        int pb = Clamp(_aB + (_bB * u) + (_cB * v));
        int pg = Clamp(_aG + (_bG * u) + (_cG * v));
        int pr = Clamp(_aR + (_bR * u) + (_cR * v));
        int db = Math.Abs(b - pb);
        int dg = Math.Abs(g - pg);
        int dr = Math.Abs(r - pr);
        return Math.Max(db, Math.Max(dg, dr));
    }

    /// <summary>
    /// Fills every pixel marked in <paramref name="mask"/> with the plane prediction,
    /// leaving all other pixels untouched. Mask coordinates are local to
    /// (<paramref name="left"/>, <paramref name="top"/>).
    /// </summary>
    public bool FillMask(
        Span<byte> pixels,
        int stride,
        int width,
        int height,
        ReadOnlySpan<byte> mask,
        int maskWidth,
        int maskHeight,
        int left,
        int top)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maskHeight);
        if (maskWidth <= 0 || maskHeight <= 0 || mask.Length < checked(maskWidth * maskHeight))
        {
            return false;
        }

        for (int y = 0; y < maskHeight; y++)
        {
            int py = top + y;
            if (py < 0 || py >= height)
            {
                continue;
            }

            int maskRow = y * maskWidth;
            int pixelRow = py * stride;
            for (int x = 0; x < maskWidth; x++)
            {
                if (mask[maskRow + x] == 0)
                {
                    continue;
                }

                int px = left + x;
                if (px < 0 || px >= width)
                {
                    continue;
                }

                double u = px - _cx;
                double v = py - _cy;
                int i = pixelRow + (px * 4);
                pixels[i] = Clamp(_aB + (_bB * u) + (_cB * v));
                pixels[i + 1] = Clamp(_aG + (_bG * u) + (_cG * v));
                pixels[i + 2] = Clamp(_aR + (_bR * u) + (_cR * v));
                pixels[i + 3] = 255;
            }
        }

        return true;
    }

    /// <summary>Fills a whole rect with the plane prediction (flat/gradient erase fallback).</summary>
    public void FillRect(
        Span<byte> pixels,
        int stride,
        int width,
        int height,
        int x0,
        int y0,
        int x1,
        int y1)
    {
        x0 = Math.Clamp(x0, 0, width);
        y0 = Math.Clamp(y0, 0, height);
        x1 = Math.Clamp(x1, 0, width);
        y1 = Math.Clamp(y1, 0, height);
        for (int y = y0; y < y1; y++)
        {
            int row = y * stride;
            double v = y - _cy;
            for (int x = x0; x < x1; x++)
            {
                double u = x - _cx;
                int i = row + (x * 4);
                pixels[i] = Clamp(_aB + (_bB * u) + (_cB * v));
                pixels[i + 1] = Clamp(_aG + (_bG * u) + (_cG * v));
                pixels[i + 2] = Clamp(_aR + (_bR * u) + (_cR * v));
                pixels[i + 3] = 255;
            }
        }
    }

    private static bool TrySolve(
        IReadOnlyList<BackgroundSample> samples,
        bool[] used,
        int usedCount,
        double cx,
        double cy,
        out double[] coefficients,
        out double[] residuals)
    {
        coefficients = null;
        residuals = new double[samples.Count];
        if (usedCount < MinSamples)
        {
            return false;
        }

        double n = 0, su = 0, sv = 0, suu = 0, suv = 0, svv = 0;
        double sb = 0, sg = 0, sr = 0, sub = 0, sug = 0, sur = 0, svb = 0, svg = 0, svr = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            if (!used[i])
            {
                continue;
            }

            var s = samples[i];
            double u = s.X - cx;
            double v = s.Y - cy;
            n++;
            su += u;
            sv += v;
            suu += u * u;
            suv += u * v;
            svv += v * v;
            sb += s.B;
            sg += s.G;
            sr += s.R;
            sub += u * s.B;
            sug += u * s.G;
            sur += u * s.R;
            svb += v * s.B;
            svg += v * s.G;
            svr += v * s.R;
        }

        double determinant = Determinant(n, su, sv, su, suu, suv, sv, suv, svv);
        if (Math.Abs(determinant) < 1e-6)
        {
            return false;
        }

        double[] Solve(double sc, double suc, double svc)
        {
            // Cramer's rule with the RHS (sc, suc, svc) replacing one column at a time.
            double a = Determinant(sc, su, sv, suc, suu, suv, svc, suv, svv) / determinant;
            double b = Determinant(n, sc, sv, su, suc, suv, sv, svc, svv) / determinant;
            double c = Determinant(n, su, sc, su, suu, suc, sv, suv, svc) / determinant;
            return [a, b, c];
        }

        double[] bcoef = Solve(sb, sub, svb);
        double[] gcoef = Solve(sg, sug, svg);
        double[] rcoef = Solve(sr, sur, svr);

        coefficients = new double[9];
        Array.Copy(bcoef, 0, coefficients, 0, 3);
        Array.Copy(gcoef, 0, coefficients, 3, 3);
        Array.Copy(rcoef, 0, coefficients, 6, 3);

        for (int i = 0; i < samples.Count; i++)
        {
            if (!used[i])
            {
                continue;
            }

            var s = samples[i];
            double u = s.X - cx;
            double v = s.Y - cy;
            double eb = Math.Abs(s.B - (bcoef[0] + (bcoef[1] * u) + (bcoef[2] * v)));
            double eg = Math.Abs(s.G - (gcoef[0] + (gcoef[1] * u) + (gcoef[2] * v)));
            double er = Math.Abs(s.R - (rcoef[0] + (rcoef[1] * u) + (rcoef[2] * v)));
            residuals[i] = Math.Max(eb, Math.Max(eg, er));
        }

        // Unused samples keep residual 0 so they are never treated as outliers again.
        return true;
    }

    private static double Determinant(
        double a,
        double b,
        double c,
        double d,
        double e,
        double f,
        double g,
        double h,
        double i)
        => (a * ((e * i) - (f * h))) - (b * ((d * i) - (f * g))) + (c * ((d * h) - (e * g)));

    private static byte Clamp(double v)
        => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
