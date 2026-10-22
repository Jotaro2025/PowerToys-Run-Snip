// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
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
    private static void RunOnStaThread(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw failure;
        }
    }

    private static T RunOnStaThread<T>(Func<T> action)
    {
        T result = default;
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw failure;
        }

        return result;
    }

    [TestMethod]
    public void ExpandBoxGrowsOutwardThenClamps()
    {
        var box = new Rect(10, 10, 20, 8);
        var expanded = ImageTranslateRenderer.ExpandBox(box, 3, 2, width: 100, height: 100);
        Assert.AreEqual(7, expanded.X, 0.01);
        Assert.AreEqual(8, expanded.Y, 0.01);
        Assert.AreEqual(26, expanded.Width, 0.01);
        Assert.AreEqual(12, expanded.Height, 0.01);

        var nearEdge = ImageTranslateRenderer.ExpandBox(new Rect(0, 0, 5, 5), 3, 2, 10, 10);
        Assert.AreEqual(0, nearEdge.X, 0.01);
        Assert.AreEqual(0, nearEdge.Y, 0.01);
        Assert.IsTrue(nearEdge.Right <= 10);
        Assert.IsTrue(nearEdge.Bottom <= 10);
    }

    [TestMethod]
    public void MedianColorIgnoresOutlierChannelValues()
    {
        // Nine samples of light gray + one bright red outlier → median stays gray-ish,
        // whereas a mean would shift red upward.
        var samples = new List<(byte R, byte G, byte B)>();
        for (int i = 0; i < 9; i++)
        {
            samples.Add((200, 200, 200));
        }

        samples.Add((255, 0, 0));

        var median = ImageTranslateRenderer.MedianColor(samples);
        Assert.AreEqual(200, median.R);
        Assert.AreEqual(200, median.G);
        Assert.AreEqual(200, median.B);

        // Mean of R would be (200*9 + 255)/10 = 205.5 → 205; median is exactly 200.
        int meanR = ((200 * 9) + 255) / 10;
        Assert.AreNotEqual((byte)meanR, median.R);
    }

    [TestMethod]
    public void WrapTextBreaksLongLatinAcrossWords()
    {
        RunOnStaThread(() =>
        {
            const string text = "hello world from snip translator";
            // Narrow width forces multiple lines at a readable font size.
            var wrapped = ImageTranslateRenderer.WrapText(text, maxWidth: 50, fontSize: 14);
            Assert.IsTrue(wrapped.Contains('\n'), $"expected wrap, got: {wrapped}");
            Assert.IsFalse(wrapped.Contains("hello world from snip translator", StringComparison.Ordinal));
            StringAssert.Contains(wrapped, "hello");
            StringAssert.Contains(wrapped, "world");
        });
    }

    [TestMethod]
    public void WrapTextBreaksLongCjkByCharacter()
    {
        RunOnStaThread(() =>
        {
            const string text = "这是一段比较长的中文翻译文本用于测试换行";
            var wrapped = ImageTranslateRenderer.WrapText(text, maxWidth: 40, fontSize: 14);
            Assert.IsTrue(wrapped.Contains('\n'), $"expected CJK wrap, got: {wrapped}");
            Assert.AreEqual(text, wrapped.Replace("\n", string.Empty, StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void FitFontSizeRespectsMinimum()
    {
        RunOnStaThread(() =>
        {
            var size = ImageTranslateRenderer.FitFontSize(
                "tiny box cannot fit much text even when wrapped into many lines",
                maxWidth: 12,
                maxHeight: 10);
            Assert.AreEqual(ImageTranslateRenderer.MinFontSize, size, 0.01);
        });
    }

    [TestMethod]
    public void FitFontSizeIsStableAcrossCalls()
    {
        RunOnStaThread(() =>
        {
            const string text = "缓存量字应保持字号稳定 hello 中文";
            double a = ImageTranslateRenderer.FitFontSize(text, 120, 48);
            double b = ImageTranslateRenderer.FitFontSize(text, 120, 48);
            Assert.AreEqual(a, b, 0.01);
            Assert.IsTrue(a >= ImageTranslateRenderer.MinFontSize);
        });
    }

    [TestMethod]
    public void SampleBackgroundPrefersSurroundingFillColor()
    {
        RunOnStaThread(() =>
        {
            // 40x40: green field, red rectangle in the center (simulates text ink / noise).
            const int w = 40;
            const int h = 40;
            var pixels = new byte[w * h * 4];
            int stride = w * 4;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * stride) + (x * 4);
                    bool inside = x >= 12 && x < 28 && y >= 14 && y < 26;
                    if (inside)
                    {
                        pixels[i] = 0;       // B
                        pixels[i + 1] = 0;   // G
                        pixels[i + 2] = 255; // R
                        pixels[i + 3] = 255;
                    }
                    else
                    {
                        pixels[i] = 40;
                        pixels[i + 1] = 180;
                        pixels[i + 2] = 40;
                        pixels[i + 3] = 255;
                    }
                }
            }

            var box = new Rect(12, 14, 16, 12);
            var bg = ImageTranslateRenderer.SampleBackground(pixels, stride, w, h, box);

            // Multi-ring median should land on the green surround, not the red interior.
            Assert.IsTrue(bg.G > bg.R + 40, $"expected greenish bg, got {bg}");
            Assert.IsTrue(bg.G > bg.B + 40, $"expected greenish bg, got {bg}");
        });
    }

    [TestMethod]
    public void RenderDrawsOnSyntheticBitmapWithoutThrowing()
    {
        var result = RunOnStaThread(() =>
        {
            var source = CreateSolidBitmap(80, 40, Colors.WhiteSmoke);
            var lines = new[]
            {
                new OcrTextLine("hello", new Rect(8, 10, 64, 18), 0.9f),
            };
            var translations = new[] { "A somewhat longer English translation that should wrap" };
            return ImageTranslateRenderer.Render(source, lines, translations);
        });

        Assert.IsNotNull(result);
        Assert.AreEqual(80, result.PixelWidth);
        Assert.AreEqual(40, result.PixelHeight);
    }

    [TestMethod]
    public void RenderRejectsMismatchedCounts()
    {
        RunOnStaThread(() =>
        {
            var source = CreateSolidBitmap(16, 16, Colors.White);
            var lines = new[] { new OcrTextLine("a", new Rect(1, 1, 10, 8), 1f) };
            Assert.ThrowsException<ArgumentException>(() =>
                ImageTranslateRenderer.Render(source, lines, new[] { "one", "two" }));
        });
    }

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
        var result = RunOnStaThread(() =>
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
            return ImageTranslateRenderer.Render(bmp, new[] { line }, new[] { "Hi" });
        });
        Assert.IsNotNull(result);
        Assert.AreEqual(80, result.PixelWidth);
        Assert.AreEqual(60, result.PixelHeight);
    }

    [TestMethod]
    public void RenderFallsBackToAxisAlignedWithoutCorners()
    {
        var result = RunOnStaThread(() =>
        {
            var bmp = CreateSolidBitmap(64, 48, Colors.White);
            var line = new OcrTextLine("源", new Rect(5, 5, 40, 16), 0.9f);
            return ImageTranslateRenderer.Render(bmp, new[] { line }, new[] { "Hi" });
        });
        Assert.IsNotNull(result);
        Assert.AreEqual(64, result.PixelWidth);
    }

    private static BitmapSource CreateSolidBitmap(int width, int height, Color color)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height));
        }

        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
