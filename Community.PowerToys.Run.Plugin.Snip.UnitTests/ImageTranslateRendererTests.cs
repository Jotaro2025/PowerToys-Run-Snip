// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class ImageTranslateRendererTests
{
    [TestMethod]
    public void IsUsableQuadAcceptsRotatedRectangle()
    {
        // Roughly 40×12 rectangle rotated ~30° around origin, then shifted.
        var corners = new[]
        {
            new Point(20, 30),
            new Point(54.64, 50),
            new Point(48.64, 60.39),
            new Point(14, 40.39),
        };
        Assert.IsTrue(ImageTranslateRenderer.IsUsableQuad(corners, out double w, out double h));
        Assert.IsTrue(w > 10);
        Assert.IsTrue(h > 5);
    }

    [TestMethod]
    public void IsUsableQuadRejectsDegenerate()
    {
        var collapsed = new[]
        {
            new Point(0, 0),
            new Point(1, 0),
            new Point(1, 0.1),
            new Point(0, 0.1),
        };
        Assert.IsFalse(ImageTranslateRenderer.IsUsableQuad(collapsed, out _, out _));

        Assert.IsFalse(ImageTranslateRenderer.IsUsableQuad(null, out _, out _));
        Assert.IsFalse(ImageTranslateRenderer.IsUsableQuad(Array.Empty<Point>(), out _, out _));
    }

    [TestMethod]
    public void CreateAffineFromQuadMapsCorners()
    {
        var quad = new[]
        {
            new Point(10, 20),
            new Point(50, 20),
            new Point(50, 40),
            new Point(10, 40),
        };
        Assert.IsTrue(ImageTranslateRenderer.IsUsableQuad(quad, out double w, out double h));
        var m = ImageTranslateRenderer.CreateAffineFromQuad(quad, w, h);

        var tl = m.Transform(new Point(0, 0));
        var tr = m.Transform(new Point(w, 0));
        var bl = m.Transform(new Point(0, h));
        Assert.AreEqual(10, tl.X, 0.01);
        Assert.AreEqual(20, tl.Y, 0.01);
        Assert.AreEqual(50, tr.X, 0.01);
        Assert.AreEqual(20, tr.Y, 0.01);
        Assert.AreEqual(10, bl.X, 0.01);
        Assert.AreEqual(40, bl.Y, 0.01);
    }

    [TestMethod]
    public void TryGetUsableQuadFallsBackWhenMissingCorners()
    {
        var line = new OcrTextLine("hi", new Rect(1, 2, 30, 10), 0.9f);
        Assert.IsFalse(ImageTranslateRenderer.TryGetUsableQuad(line, 100, 100, out _, out _, out _));
    }

    [TestMethod]
    public void TryGetUsableQuadUsesSyntheticCorners()
    {
        var corners = new[]
        {
            new Point(10, 10),
            new Point(60, 10),
            new Point(60, 28),
            new Point(10, 28),
        };
        var line = new OcrTextLine("hi", new Rect(10, 10, 50, 18), 0.9f, corners);
        Assert.IsTrue(ImageTranslateRenderer.TryGetUsableQuad(line, 200, 200, out var quad, out var w, out var h));
        Assert.AreEqual(4, quad.Length);
        Assert.IsTrue(w >= 40);
        Assert.IsTrue(h >= 10);
    }

    [TestMethod]
    public void RenderWithQuadCornersProducesBitmap()
    {
        var bmp = CreateSolidBitmap(80, 60, Colors.LightGray);
        var corners = new[]
        {
            new Point(8, 20),
            new Point(55, 8),
            new Point(60, 28),
            new Point(13, 40),
        };
        var line = new OcrTextLine("源", new Rect(8, 8, 52, 32), 0.95f, corners);
        var result = ImageTranslateRenderer.Render(bmp, new[] { line }, new[] { "Hi" });
        Assert.IsNotNull(result);
        Assert.AreEqual(80, result.PixelWidth);
        Assert.AreEqual(60, result.PixelHeight);
    }

    [TestMethod]
    public void RenderFallsBackToAxisAlignedWithoutCorners()
    {
        var bmp = CreateSolidBitmap(64, 48, Colors.White);
        var line = new OcrTextLine("源", new Rect(5, 5, 40, 16), 0.9f);
        var result = ImageTranslateRenderer.Render(bmp, new[] { line }, new[] { "Hi" });
        Assert.IsNotNull(result);
        Assert.AreEqual(64, result.PixelWidth);
    }

    private static WriteableBitmap CreateSolidBitmap(int width, int height, Color color)
    {
        var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }

        bmp.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        bmp.Freeze();
        return bmp;
    }
}
