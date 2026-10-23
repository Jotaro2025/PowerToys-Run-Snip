// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class Radix2FftTests
{
    [TestMethod]
    public void ForwardInverse_NoScaling_RecoversNTimesInput()
    {
        var data = new ComplexF[16];
        var original = new ComplexF[16];
        for (int i = 0; i < data.Length; i++)
        {
            float re = (float)Math.Sin(i * 0.37);
            float im = (float)Math.Cos(i * 0.19);
            data[i] = new ComplexF(re, im);
            original[i] = data[i];
        }

        Radix2Fft.Forward(data);
        Radix2Fft.Inverse(data);

        for (int i = 0; i < data.Length; i++)
        {
            float expectedRe = original[i].Real * data.Length;
            float expectedIm = original[i].Imaginary * data.Length;
            Assert.AreEqual(expectedRe, data[i].Real, 1e-3f, $"re[{i}]");
            Assert.AreEqual(expectedIm, data[i].Imaginary, 1e-3f, $"im[{i}]");
        }
    }

    [TestMethod]
    public void Forward_Impulse_ProducesConstantMagnitude()
    {
        var data = new ComplexF[32];
        data[5] = new ComplexF(1, 0);

        Radix2Fft.Forward(data);

        for (int k = 0; k < data.Length; k++)
        {
            Assert.AreEqual(1f, data[k].Magnitude, 1e-4f, $"mag[{k}]");
        }
    }

    [TestMethod]
    public void Forward_RejectsNonPowerOfTwoLength()
    {
        var data = new ComplexF[12];
        Assert.ThrowsException<ArgumentException>(() => Radix2Fft.Forward(data));
    }
}

[TestClass]
public class ScrollPhaseCorrelatorTests
{
    private const int Width = 64;
    private const int Height = 96;
    private const int Stride = Width * 4;

    [TestMethod]
    public unsafe void EstimateVerticalShift_DetectsKnownDownwardScroll()
    {
        const int dh = 20;
        byte[] prev = BuildEdgeRichFrame();
        byte[] next = ShiftContentUp(prev, dh);

        fixed (byte* pPrev = prev)
        fixed (byte* pNext = next)
        {
            var estimate = ScrollPhaseCorrelator.EstimateVerticalShift(
                pPrev, Stride, Height,
                pNext, Stride, Height,
                Width, minDh: 4, maxDh: 60, scrollHintPx: dh);

            Assert.IsTrue(estimate.Success, $"peak={estimate.Peak} dh={estimate.Dh}");
            Assert.IsTrue(Math.Abs(estimate.Dh - dh) <= 2, $"dh={estimate.Dh} peak={estimate.Peak}");
        }
    }

    [TestMethod]
    public unsafe void EstimateVerticalShift_FailsOnUnrelatedFrames()
    {
        byte[] a = BuildEdgeRichFrame();
        // Pure vertical banding → FillHorizontalEdge ≈ 0, so phase correlation must fail.
        byte[] b = BuildVerticalBandFrame();

        fixed (byte* pa = a)
        fixed (byte* pb = b)
        {
            var estimate = ScrollPhaseCorrelator.EstimateVerticalShift(
                pa, Stride, Height,
                pb, Stride, Height,
                Width, minDh: 4, maxDh: 60, scrollHintPx: 0);

            Assert.IsFalse(estimate.Success, $"unexpected Success peak={estimate.Peak} dh={estimate.Dh}");
        }
    }

    /// <summary>
    /// Rows vary but each row is constant → no horizontal luminance steps for the correlator.
    /// </summary>
    private static byte[] BuildVerticalBandFrame()
    {
        var buf = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
        {
            byte v = (byte)(((y / 3) % 2 == 0) ? 30 + (y % 40) : 220 - (y % 30));
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Stride) + (x * 4);
                buf[o] = v;
                buf[o + 1] = v;
                buf[o + 2] = v;
                buf[o + 3] = 255;
            }
        }

        return buf;
    }

    /// <summary>
    /// Horizontal + vertical structure so FillHorizontalEdge has signal after a pure vertical shift.
    /// </summary>
    private static byte[] BuildEdgeRichFrame()
    {
        var buf = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Stride) + (x * 4);
                // Checker / stripe mix → strong horizontal luminance steps.
                byte v = (byte)((((x / 4) + (y / 3)) % 2 == 0) ? 40 + (y % 50) : 200 - (y % 40));
                buf[o] = v;
                buf[o + 1] = (byte)(255 - v);
                buf[o + 2] = (byte)((x * 5) % 256);
                buf[o + 3] = 255;
            }
        }

        return buf;
    }

    /// <summary>
    /// next[y] = prev[y+dh] for the overlap (page scrolled down / content moved up).
    /// </summary>
    private static byte[] ShiftContentUp(byte[] prev, int dh)
    {
        var next = new byte[Stride * Height];
        int copyRows = Height - dh;
        for (int y = 0; y < copyRows; y++)
        {
            prev.AsSpan((y + dh) * Stride, Stride).CopyTo(next.AsSpan(y * Stride, Stride));
        }

        for (int y = copyRows; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Stride) + (x * 4);
                next[o] = (byte)(y + 3);
                next[o + 1] = (byte)(x + 5);
                next[o + 2] = (byte)((y * x) % 256);
                next[o + 3] = 255;
            }
        }

        return next;
    }
}
