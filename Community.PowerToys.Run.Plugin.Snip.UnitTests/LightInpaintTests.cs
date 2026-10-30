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
        Assert.AreEqual(0, LightInpaint.LastRegionMaskLength, "early exit must not allocate a region mask");
    }

    [TestMethod]
    public void RegionMaskIsLocalNotFullImage()
    {
        // Large canvas, small OCR box — mask must be rw*rh, not W*H.
        const int bigW = 1920;
        const int bigH = 1080;
        const int stride = bigW * 4;
        var pixels = new byte[stride * bigH];
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i - 1] = 120;
            pixels[i - 2] = 120;
            pixels[i - 3] = 120;
        }

        int x0 = 100, y0 = 200, x1 = 140, y1 = 230; // 40×30 = 1200
        int rw = x1 - x0;
        int rh = y1 - y0;
        Assert.IsTrue(LightInpaint.TryInpaintRect(pixels, stride, bigW, bigH, x0, y0, x1, y1));
        Assert.AreEqual(LightInpaint.RegionMaskLength(rw, rh), LightInpaint.LastRegionMaskLength);
        Assert.IsTrue(
            LightInpaint.LastRegionMaskLength < bigW * bigH / 100,
            $"region mask {LightInpaint.LastRegionMaskLength} should be << full image {bigW * bigH}");
    }

    [TestMethod]
    public void RegionMaskLengthHelperMatchesProduct()
    {
        Assert.AreEqual(40 * 30, LightInpaint.RegionMaskLength(40, 30));
        _ = Assert.ThrowsException<ArgumentOutOfRangeException>(() => LightInpaint.RegionMaskLength(0, 10));
    }

    [TestMethod]
    public void InpaintFillsEntireBoxLeavingOpaquePixels()
    {
        var truth = CreateHorizontalGradient();
        var buf = (byte[])truth.Clone();
        int x0 = 20, y0 = 10, x1 = 60, y1 = 30;
        PaintCheckerText(buf, x0, y0, x1, y1);
        Assert.IsTrue(LightInpaint.TryInpaintRect(buf, Stride, Width, Height, x0, y0, x1, y1));
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * Stride) + (x * 4);
                Assert.AreEqual(255, buf[i + 3], "inpainted pixels stay opaque");
            }
        }
    }

    [TestMethod]
    public void MaskInpaintLeavesUnmaskedPixelsUntouched()
    {
        var truth = CreateHorizontalGradient();
        var buffer = (byte[])truth.Clone();
        int x0 = 20, y0 = 10, x1 = 60, y1 = 30;
        PaintCheckerText(buffer, x0, y0, x1, y1);
        var before = (byte[])buffer.Clone();

        // Region-local mask (mask[0,0] == image (x0,y0)): only the left half of the rect.
        int rw = x1 - x0;
        int rh = y1 - y0;
        var mask = new byte[rw * rh];
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw / 2; x++)
            {
                mask[(y * rw) + x] = 1;
            }
        }

        Assert.IsTrue(LightInpaint.TryInpaintMask(buffer, Stride, Width, Height, mask, rw, x0, y0, x1, y1));

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                bool masked = x >= x0 && x < x1 && y >= y0 && y < y1
                    && mask[((y - y0) * rw) + (x - x0)] != 0;
                if (masked)
                {
                    continue;
                }

                int i = (y * Stride) + (x * 4);
                Assert.AreEqual(before[i], buffer[i], $"B at ({x},{y}) must stay untouched");
                Assert.AreEqual(before[i + 1], buffer[i + 1], $"G at ({x},{y}) must stay untouched");
                Assert.AreEqual(before[i + 2], buffer[i + 2], $"R at ({x},{y}) must stay untouched");
            }
        }
    }

    [TestMethod]
    public void MaskInpaintOnGradientReconstructsStrokes()
    {
        var truth = CreateHorizontalGradient();
        var buffer = (byte[])truth.Clone();
        int x0 = 20, y0 = 10, x1 = 60, y1 = 30;
        PaintCheckerText(buffer, x0, y0, x1, y1);

        int rw = x1 - x0;
        int rh = y1 - y0;
        var mask = new byte[rw * rh];
        Array.Fill(mask, (byte)1);

        Assert.IsTrue(LightInpaint.TryInpaintMask(buffer, Stride, Width, Height, mask, rw, x0, y0, x1, y1));

        double mae = LightInpaint.MeanAbsErrorBgr(buffer, truth, Stride, x0, y0, x1, y1);
        Assert.IsTrue(mae < 12, $"mask inpaint MAE {mae:F2} should stay low on a gradient");
    }

    [TestMethod]
    public void EmptyMaskIsNoOp()
    {
        var buffer = CreateHorizontalGradient();
        var before = (byte[])buffer.Clone();
        int rw = 50;
        int rh = 20;
        var mask = new byte[rw * rh];

        Assert.IsTrue(LightInpaint.TryInpaintMask(buffer, Stride, Width, Height, mask, rw, 10, 10, 60, 30));
        CollectionAssert.AreEqual(before, buffer);
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
