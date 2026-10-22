// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class LightInpaintTests
{
    private const int Width = 80;
    private const int Height = 40;
    private const int Stride = Width * 4;

    [TestMethod]
    public void InpaintOnGradientBeatsMedianFillMae()
    {
        var truth = CreateHorizontalGradient();
        var corrupted = (byte[])truth.Clone();

        // High-contrast "text" residue: alternating black/white columns in the box.
        int x0 = 20, y0 = 10, x1 = 60, y1 = 30;
        PaintCheckerText(corrupted, x0, y0, x1, y1);

        var medianBuf = (byte[])corrupted.Clone();
        var inpaintBuf = (byte[])corrupted.Clone();

        // Median of border rings ≈ mid-gray for this gradient; solid fill.
        byte medB = 128, medG = 128, medR = 128;
        LightInpaint.FillRectSolid(medianBuf, Stride, Width, Height, x0, y0, x1, y1, medB, medG, medR);

        Assert.IsTrue(
            LightInpaint.TryInpaintRect(inpaintBuf, Stride, Width, Height, x0, y0, x1, y1),
            "inpaint should succeed on a bordered gradient hole");

        double maeMedian = LightInpaint.MeanAbsErrorBgr(medianBuf, truth, Stride, x0, y0, x1, y1);
        double maeInpaint = LightInpaint.MeanAbsErrorBgr(inpaintBuf, truth, Stride, x0, y0, x1, y1);

        Assert.IsTrue(
            maeInpaint < maeMedian * 0.85,
            $"expected inpaint MAE ({maeInpaint:F2}) clearly below median MAE ({maeMedian:F2})");
    }

    [TestMethod]
    public void InpaintOnNoiseReducesHighFreqVsMedian()
    {
        var truth = CreateSmoothNoise(seed: 42);
        var corrupted = (byte[])truth.Clone();

        int x0 = 18, y0 = 8, x1 = 62, y1 = 32;
        PaintCheckerText(corrupted, x0, y0, x1, y1);

        var medianBuf = (byte[])corrupted.Clone();
        var inpaintBuf = (byte[])corrupted.Clone();

        // Approximate border median from truth (known-good).
        SampleBorderMedian(truth, x0, y0, x1, y1, out byte mb, out byte mg, out byte mr);
        LightInpaint.FillRectSolid(medianBuf, Stride, Width, Height, x0, y0, x1, y1, mb, mg, mr);

        Assert.IsTrue(LightInpaint.TryInpaintRect(inpaintBuf, Stride, Width, Height, x0, y0, x1, y1));

        double hfMedian = LightInpaint.HighFreqEnergy(medianBuf, Stride, Width, Height, x0, y0, x1, y1);
        double hfInpaint = LightInpaint.HighFreqEnergy(inpaintBuf, Stride, Width, Height, x0, y0, x1, y1);
        double hfCorrupt = LightInpaint.HighFreqEnergy(corrupted, Stride, Width, Height, x0, y0, x1, y1);

        Assert.IsTrue(hfCorrupt > hfInpaint, "inpaint should reduce checker high-freq vs corrupted text");
        Assert.IsTrue(
            hfInpaint <= hfMedian * 1.05,
            $"inpaint HF ({hfInpaint:F0}) should not exceed median HF ({hfMedian:F0}) by much");

        double maeMedian = LightInpaint.MeanAbsErrorBgr(medianBuf, truth, Stride, x0, y0, x1, y1);
        double maeInpaint = LightInpaint.MeanAbsErrorBgr(inpaintBuf, truth, Stride, x0, y0, x1, y1);
        Assert.IsTrue(
            maeInpaint < maeMedian,
            $"inpaint MAE ({maeInpaint:F2}) should beat solid median ({maeMedian:F2}) on smooth noise");
    }

    [TestMethod]
    public void TinyRegionFallsBack()
    {
        var buf = CreateHorizontalGradient();
        Assert.IsFalse(LightInpaint.TryInpaintRect(buf, Stride, Width, Height, 10, 10, 12, 12));
    }

    private static byte[] CreateHorizontalGradient()
    {
        var pixels = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte v = (byte)(x * 255 / Math.Max(1, Width - 1));
                int i = (y * Stride) + (x * 4);
                pixels[i] = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }

    private static byte[] CreateSmoothNoise(int seed)
    {
        var rng = new Random(seed);
        var pixels = new byte[Stride * Height];

        // Low-frequency value noise via bilinear of a coarse grid.
        const int gw = 8, gh = 4;
        var grid = new byte[gw * gh];
        for (int i = 0; i < grid.Length; i++)
        {
            grid[i] = (byte)rng.Next(40, 220);
        }

        for (int y = 0; y < Height; y++)
        {
            double gy = y * (gh - 1.0) / Math.Max(1, Height - 1);
            int y0 = (int)Math.Floor(gy);
            int y1 = Math.Min(gh - 1, y0 + 1);
            double ty = gy - y0;
            for (int x = 0; x < Width; x++)
            {
                double gx = x * (gw - 1.0) / Math.Max(1, Width - 1);
                int x0 = (int)Math.Floor(gx);
                int x1 = Math.Min(gw - 1, x0 + 1);
                double tx = gx - x0;
                double v00 = grid[(y0 * gw) + x0];
                double v10 = grid[(y0 * gw) + x1];
                double v01 = grid[(y1 * gw) + x0];
                double v11 = grid[(y1 * gw) + x1];
                double v0 = v00 + ((v10 - v00) * tx);
                double v1 = v01 + ((v11 - v01) * tx);
                byte v = (byte)Math.Clamp((int)Math.Round(v0 + ((v1 - v0) * ty)), 0, 255);
                int i = (y * Stride) + (x * 4);
                pixels[i] = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }

    private static void PaintCheckerText(byte[] pixels, int x0, int y0, int x1, int y1)
    {
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                byte v = ((x + y) & 1) == 0 ? (byte)0 : (byte)255;
                int i = (y * Stride) + (x * 4);
                pixels[i] = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
        }
    }

    private static void SampleBorderMedian(byte[] pixels, int x0, int y0, int x1, int y1, out byte b, out byte g, out byte r)
    {
        long sb = 0, sg = 0, sr = 0, n = 0;
        void Acc(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height)
            {
                return;
            }

            int i = (y * Stride) + (x * 4);
            sb += pixels[i];
            sg += pixels[i + 1];
            sr += pixels[i + 2];
            n++;
        }

        for (int x = x0 - 2; x < x1 + 2; x++)
        {
            Acc(x, y0 - 2);
            Acc(x, y1 + 1);
        }

        for (int y = y0; y < y1; y++)
        {
            Acc(x0 - 2, y);
            Acc(x1 + 1, y);
        }

        if (n == 0)
        {
            b = g = r = 128;
            return;
        }

        b = (byte)(sb / n);
        g = (byte)(sg / n);
        r = (byte)(sr / n);
    }
}
