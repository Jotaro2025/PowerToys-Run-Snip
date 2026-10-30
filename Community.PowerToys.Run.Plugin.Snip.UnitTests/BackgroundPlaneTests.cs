// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class BackgroundPlaneTests
{
    [TestMethod]
    public void FitRecoversLinearGradient()
    {
        var samples = new List<BackgroundSample>();
        for (int y = 0; y <= 40; y += 4)
        {
            for (int x = 0; x <= 200; x += 10)
            {
                byte v = (byte)Math.Clamp(20 + (x / 2), 0, 255);
                samples.Add(new BackgroundSample(x, y, v, v, v));
            }
        }

        var plane = BackgroundPlane.Fit(samples);

        Assert.IsNotNull(plane);
        Assert.IsTrue(plane.Rms < 1.5, $"gradient should fit almost exactly, rms={plane.Rms}");

        var left = plane.At(10, 20);
        var right = plane.At(190, 20);
        Assert.IsTrue(right.R > left.R + 60, $"plane must follow the gradient ({left.R} → {right.R})");
        Assert.IsTrue(Math.Abs(left.R - 25) <= 3, $"left prediction {left.R} should be near 25");
        Assert.IsTrue(Math.Abs(right.R - 115) <= 3, $"right prediction {right.R} should be near 115");
    }

    [TestMethod]
    public void FitRejectsRandomNoise()
    {
        var random = new Random(11);
        var samples = new List<BackgroundSample>();
        for (int i = 0; i < 120; i++)
        {
            byte v = (byte)random.Next(0, 256);
            samples.Add(new BackgroundSample(random.Next(0, 200), random.Next(0, 40), v, v, v));
        }

        Assert.IsNull(BackgroundPlane.Fit(samples), "random texture must not be accepted as a plane");
    }

    [TestMethod]
    public void FitNeedsEnoughSamples()
    {
        var samples = new List<BackgroundSample>
        {
            new BackgroundSample(0, 0, 10, 10, 10),
            new BackgroundSample(1, 0, 10, 10, 10),
            new BackgroundSample(2, 0, 10, 10, 10),
        };

        Assert.IsNull(BackgroundPlane.Fit(samples));
    }

    [TestMethod]
    public void FillRectWritesPlaneValues()
    {
        var samples = new List<BackgroundSample>();
        for (int y = 0; y <= 20; y += 2)
        {
            for (int x = 0; x <= 100; x += 5)
            {
                byte v = (byte)(10 + x);
                samples.Add(new BackgroundSample(x, y, v, v, v));
            }
        }

        var plane = BackgroundPlane.Fit(samples);
        Assert.IsNotNull(plane);

        const int width = 120;
        const int height = 40;
        var pixels = new byte[width * height * 4];
        plane.FillRect(pixels, width * 4, width, height, 40, 10, 80, 30);

        for (int y = 10; y < 30; y++)
        {
            for (int x = 40; x < 80; x++)
            {
                int i = ((y * width) + x) * 4;
                Assert.AreEqual(plane.At(x, y).B, pixels[i]);
                Assert.AreEqual(plane.At(x, y).G, pixels[i + 1]);
                Assert.AreEqual(plane.At(x, y).R, pixels[i + 2]);
                Assert.AreEqual(255, pixels[i + 3]);
            }
        }

        // Outside the rect stays untouched.
        Assert.AreEqual(0, pixels[0]);
    }

    [TestMethod]
    public void FillMaskOnlyWritesMaskedPixels()
    {
        var samples = new List<BackgroundSample>();
        for (int y = 0; y <= 20; y += 2)
        {
            for (int x = 0; x <= 100; x += 5)
            {
                samples.Add(new BackgroundSample(x, y, 50, 50, 50));
            }
        }

        var plane = BackgroundPlane.Fit(samples);
        Assert.IsNotNull(plane);

        const int width = 60;
        const int height = 40;
        var pixels = new byte[width * height * 4];
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        var mask = new byte[10 * 6];
        mask[(2 * 10) + 3] = 1;
        mask[(4 * 10) + 5] = 1;

        Assert.IsTrue(plane.FillMask(pixels, width * 4, width, height, mask, 10, 6, 20, 10));

        int filled = ((12 * width) + 23) * 4;
        Assert.AreEqual(50, pixels[filled]);
        Assert.AreEqual(50, pixels[filled + 1]);
        Assert.AreEqual(50, pixels[filled + 2]);

        int untouched = ((11 * width) + 21) * 4;
        Assert.AreEqual(0, pixels[untouched]);
        Assert.AreEqual(0, pixels[untouched + 1]);
        Assert.AreEqual(0, pixels[untouched + 2]);
    }
}
