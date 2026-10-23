// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class PixelMagnifierTests
{
    [TestMethod]
    public void SamplePixelBgraReadsAndClampsCoordinates()
    {
        const int w = 4;
        const int h = 4;
        const int stride = w * 4;
        var src = new byte[stride * h];
        int targetOff = (2 * stride) + (3 * 4);
        src[targetOff + 0] = 0x4D; // B
        src[targetOff + 1] = 0x20; // G
        src[targetOff + 2] = 0xEE; // R
        src[targetOff + 3] = 0xFF;

        PixelMagnifier.SamplePixelBgra(src, stride, w, h, px: 99, py: 2, out byte r, out byte g, out byte b);
        Assert.AreEqual(0xEE, r);
        Assert.AreEqual(0x20, g);
        Assert.AreEqual(0x4D, b);
        Assert.AreEqual("#EE204D", PixelMagnifier.FormatHex(r, g, b));
        Assert.AreEqual("RGB(238, 32, 77)", PixelMagnifier.FormatRgb(r, g, b));
    }

    [TestMethod]
    public void FillZoomedGridExpandsPixelsAndDrawsCrosshair()
    {
        const int w = 16;
        const int h = 16;
        const int stride = w * 4;
        var src = new byte[stride * h];
        for (int i = 0; i < src.Length; i += 4)
        {
            src[i + 0] = 10;
            src[i + 1] = 20;
            src[i + 2] = 30;
            src[i + 3] = 255;
        }

        int outSize = PixelMagnifier.OutputPixelSize;
        int destStride = outSize * 4;
        var dest = new byte[destStride * outSize];

        PixelMagnifier.FillZoomedGrid(src, stride, w, h, centerX: 8, centerY: 8, dest, destStride);

        // Top-left pixel (0,0) has the source color (10, 20, 30, 255).
        Assert.AreEqual(10, dest[0]);
        Assert.AreEqual(20, dest[1]);
        Assert.AreEqual(30, dest[2]);
        Assert.AreEqual(255, dest[3]);

        // Crosshair border at (50, 50) has accent green (B=110, G=210, R=72).
        int c0 = (PixelMagnifier.GridSize / 2) * PixelMagnifier.ZoomFactor;
        int crossOff = (c0 * destStride) + (c0 * 4);
        Assert.AreEqual(110, dest[crossOff + 0]);
        Assert.AreEqual(210, dest[crossOff + 1]);
        Assert.AreEqual(72, dest[crossOff + 2]);
    }

    [TestMethod]
    public void ComputeHudPositionFlipsNearBottomRightMonitorEdge()
    {
        var monitor = new Rect(0, 0, 1920, 1080);
        var hudSize = new Size(140, 180);

        var normal = PixelMagnifier.ComputeHudPosition(new Point(200, 200), hudSize, monitor);
        Assert.IsTrue(normal.X > 200 && normal.Y > 200);

        var flipped = PixelMagnifier.ComputeHudPosition(new Point(1880, 1040), hudSize, monitor);
        Assert.IsTrue(flipped.X + hudSize.Width <= monitor.Right);
        Assert.IsTrue(flipped.Y + hudSize.Height <= monitor.Bottom);
        Assert.IsTrue(flipped.X < 1880 && flipped.Y < 1040);
    }
}
