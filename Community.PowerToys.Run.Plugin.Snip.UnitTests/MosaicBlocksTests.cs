// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class MosaicBlocksTests
{
    private const int Width = 16;
    private const int Height = 16;
    private const int Stride = Width * 4;

    [TestMethod]
    public void FullAverageMatchesManualMean()
    {
        var src = CreateGradient();
        MosaicBlocks.SampleBlock(
            src,
            Stride,
            bx: 0,
            by: 0,
            blockW: 4,
            blockH: 4,
            MosaicBlocks.SampleMode.FullAverage,
            out byte b,
            out byte g,
            out byte r);

        long sumB = 0, sumG = 0, sumR = 0, count = 0;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int o = (y * Stride) + (x * 4);
                sumB += src[o];
                sumG += src[o + 1];
                sumR += src[o + 2];
                count++;
            }
        }

        Assert.AreEqual((byte)(sumB / count), b);
        Assert.AreEqual((byte)(sumG / count), g);
        Assert.AreEqual((byte)(sumR / count), r);
    }

    [TestMethod]
    public void Preview2x2UsesCenterNeighborhoodNotFullBlock()
    {
        // Left half black, right half white. An 8×8 tile starting at x=4 spans both.
        var src = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte v = x < 8 ? (byte)0 : (byte)255;
                int o = (y * Stride) + (x * 4);
                src[o] = v;
                src[o + 1] = v;
                src[o + 2] = v;
                src[o + 3] = 255;
            }
        }

        // Tile [4,12)×[0,8): half black / half white → full mean 127.
        MosaicBlocks.SampleBlock(
            src, Stride, 4, 0, 8, 8, MosaicBlocks.SampleMode.FullAverage,
            out byte fullB, out _, out _);
        // Center (8,4); 2×2 at x=8..9 is white → preview 255.
        MosaicBlocks.SampleBlock(
            src, Stride, 4, 0, 8, 8, MosaicBlocks.SampleMode.Preview2x2,
            out byte prevB, out _, out _);

        Assert.AreEqual(127, fullB);
        Assert.AreEqual(255, prevB);
        Assert.AreNotEqual(fullB, prevB);
    }

    [TestMethod]
    public void FillWritesOpaqueTilesAndCoversRegion()
    {
        var src = CreateGradient();
        int x0 = 2, y0 = 2, x1 = 10, y1 = 10;
        int dw = x1 - x0, dh = y1 - y0;
        var dest = new byte[dw * 4 * dh];

        MosaicBlocks.Fill(
            src, Stride, Width, Height,
            dest, dw * 4,
            x0, y0, x1, y1,
            blockSize: 4,
            MosaicBlocks.SampleMode.FullAverage);

        // Every dest pixel opaque; first tile uniform.
        for (int i = 0; i < dest.Length; i += 4)
        {
            Assert.AreEqual(255, dest[i + 3]);
        }

        byte b0 = dest[0], g0 = dest[1], r0 = dest[2];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int o = (y * dw * 4) + (x * 4);
                Assert.AreEqual(b0, dest[o]);
                Assert.AreEqual(g0, dest[o + 1]);
                Assert.AreEqual(r0, dest[o + 2]);
            }
        }
    }


    [TestMethod]
    public void FillGaussianBlurSmoothsSharpEdgeAndWritesOpaqueAlpha()
    {
        var src = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte v = x < 8 ? (byte)0 : (byte)255;
                int o = (y * Stride) + (x * 4);
                src[o] = v;
                src[o + 1] = v;
                src[o + 2] = v;
                src[o + 3] = 255;
            }
        }

        var dest = new byte[Stride * Height];
        MosaicBlocks.FillGaussianBlur(src, Stride, Width, Height, dest, Stride, 0, 0, Width, Height, radius: 3);

        int boundaryOff = (8 * Stride) + (8 * 4);
        Assert.IsTrue(dest[boundaryOff] > 20 && dest[boundaryOff] < 235, "Edge pixel should be blended by Gaussian/box blur.");
        Assert.AreEqual(255, dest[boundaryOff + 3]);
    }

    [TestMethod]
    public void FillSolidWritesExactColorAcrossBuffer()
    {
        int w = 6, h = 4;
        var dest = new byte[w * 4 * h];
        MosaicBlocks.FillSolid(dest, w * 4, w, h, b: 12, g: 34, r: 56, a: 255);
        for (int i = 0; i < dest.Length; i += 4)
        {
            Assert.AreEqual(12, dest[i + 0]);
            Assert.AreEqual(34, dest[i + 1]);
            Assert.AreEqual(56, dest[i + 2]);
            Assert.AreEqual(255, dest[i + 3]);
        }
    }

    private static byte[] CreateGradient()
    {
        var src = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Stride) + (x * 4);
                src[o + 0] = (byte)(x * 8); // B
                src[o + 1] = (byte)(y * 8); // G
                src[o + 2] = (byte)((x + y) * 4); // R
                src[o + 3] = 255;
            }
        }

        return src;
    }
}
