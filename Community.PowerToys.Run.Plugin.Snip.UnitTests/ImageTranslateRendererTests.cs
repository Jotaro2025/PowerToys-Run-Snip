// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
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
    public void RenderDrawsTextForEveryBoxHeight()
    {
        // Regression: a fitted font whose line box sat a fraction above the clip
        // height made FormattedText report 0×0, so the source text was erased and
        // nothing was painted (blank result image). Sweep heights so at least one
        // of them lands on that knife edge.
        RunOnStaThread(() =>
        {
            foreach (int boxHeight in new[] { 12, 14, 16, 17, 18, 19, 20, 21, 22, 24, 26 })
            {
                var source = CreateSolidBitmap(300, 90, Colors.White);
                var box = new Rect(10, 20, 200, boxHeight);
                var line = new OcrTextLine("源文", box, 0.9f);

                var rendered = ImageTranslateRenderer.Render(source, new[] { line }, new[] { "译文内容" });

                int dark = CountDarkPixels(rendered, new Int32Rect(0, 0, rendered.PixelWidth, rendered.PixelHeight));
                Assert.AreNotEqual(0, dark, $"no translated text drawn for box height {boxHeight}");
            }
        });
    }

    [TestMethod]
    public void RenderGrowsBoxSoLongTranslationStaysVisible()
    {
        RunOnStaThread(() =>
        {
            var source = CreateSolidBitmap(240, 160, Colors.White);
            var box = new Rect(10, 30, 120, 18);
            var line = new OcrTextLine("源", box, 0.9f);
            var translation = string.Concat(Enumerable.Repeat("很长的译文", 8));

            var rendered = ImageTranslateRenderer.Render(source, new[] { line }, new[] { translation });

            var bounds = DarkPixelBounds(rendered);
            Assert.IsTrue(bounds.HasValue, "no translated text drawn at all");

            // One 8 px line is ~10 px tall; several wrapped lines prove the tail was
            // kept instead of being clipped after the first line.
            Assert.IsTrue(
                bounds.Value.Height > 20,
                $"expected multiple text lines (drawn height {bounds.Value.Height:0.#})");

            // The paint box grows downward past the original OCR box.
            Assert.IsTrue(
                bounds.Value.Bottom > box.Bottom + 4,
                $"expected text below the OCR box (bottom {bounds.Value.Bottom:0.#} vs box {box.Bottom:0.#})");
        });
    }

    [TestMethod]
    public void GrowBoxToFitExpandsDownThenUpAndCaps()
    {
        var box = new Rect(10, 10, 100, 20);

        var fits = ImageTranslateRenderer.GrowBoxToFit(box, 20, width: 200, height: 200);
        Assert.AreEqual(10, fits.Y, 0.01);
        Assert.AreEqual(20, fits.Height, 0.01);

        var grown = ImageTranslateRenderer.GrowBoxToFit(box, 50, width: 200, height: 200);
        Assert.AreEqual(10, grown.Y, 0.01);
        Assert.AreEqual(50, grown.Height, 0.01);

        // Near the bottom edge the box moves up instead of overflowing.
        var nearBottom = new Rect(10, 80, 100, 20);
        var shifted = ImageTranslateRenderer.GrowBoxToFit(nearBottom, 50, width: 200, height: 100);
        Assert.AreEqual(50, shifted.Y, 0.01);
        Assert.AreEqual(50, shifted.Height, 0.01);
        Assert.IsTrue(shifted.Bottom <= 100);

        // Growth is capped so one line cannot erase an unbounded area.
        var capped = ImageTranslateRenderer.GrowBoxToFit(box, 5000, width: 200, height: 2000);
        Assert.AreEqual(20 + ImageTranslateRenderer.MaxBoxGrowth, capped.Height, 0.01);
    }

    [TestMethod]
    public void ExpandToCoverInkGrowsOverAscendersAndDescenders()
    {
        // 120x60 white page: ink rows above (y=17..19) and below (y=39..41) a box
        // y=22..38, mimicking an OCR box that hugs the x-height of the line.
        const int w = 120;
        const int h = 60;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }

        foreach (int y in new[] { 19, 20, 21, 38, 39, 40 })
        {
            for (int x = 20; x < 60; x++)
            {
                int i = (y * stride) + (x * 4);
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
            }
        }

        var box = new Rect(10, 22, 80, 16);
        var expanded = ImageTranslateRenderer.ExpandToCoverInk(pixels, stride, w, h, box, Colors.White);

        Assert.IsTrue(expanded.Top <= 19, $"top not covered: {expanded.Top}");
        Assert.IsTrue(expanded.Bottom >= 41, $"descenders not covered: {expanded.Bottom}");
        Assert.AreEqual(box.Left, expanded.Left, 0.01);
        Assert.AreEqual(box.Width, expanded.Width, 0.01);
    }

    [TestMethod]
    public void ExpandToCoverInkStopsAtBackgroundRows()
    {
        const int w = 120;
        const int h = 60;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }

        var box = new Rect(10, 22, 80, 16);
        var expanded = ImageTranslateRenderer.ExpandToCoverInk(pixels, stride, w, h, box, Colors.White);

        Assert.AreEqual(box.Y, expanded.Y, 0.01);
        Assert.AreEqual(box.Height, expanded.Height, 0.01);
    }

    [TestMethod]
    public void ExpandToCoverInkIsCapped()
    {
        const int w = 120;
        const int h = 80;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;
            pixels[i + 1] = 0;
            pixels[i + 2] = 0;
            pixels[i + 3] = 255;
        }

        var box = new Rect(10, 30, 80, 16);
        var expanded = ImageTranslateRenderer.ExpandToCoverInk(pixels, stride, w, h, box, Colors.White);

        int maxExtra = Math.Max(2, Math.Min(6, (int)Math.Ceiling(box.Height * 0.4)));
        Assert.IsTrue(
            expanded.Top >= box.Top - maxExtra - ImageTranslateRenderer.TintRowBudget,
            $"top expanded too far: top={expanded.Top} box={box.Top} maxExtra={maxExtra}");
        Assert.IsTrue(
            expanded.Bottom <= box.Bottom + maxExtra + ImageTranslateRenderer.TintRowBudget,
            $"bottom expanded too far: bottom={expanded.Bottom} box={box.Bottom} maxExtra={maxExtra}");
    }

    [TestMethod]
    public void ExpandToCoverInkCoversFlatTintStrip()
    {
        // Inline-code background: a light-grey strip one row above/below the box.
        const int w = 200;
        const int h = 80;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 1] = 255;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }

        foreach (int y in new[] { 29, 47 })
        {
            for (int x = 40; x < 120; x++)
            {
                int i = (y * stride) + (x * 4);
                pixels[i] = 243;
                pixels[i + 1] = 243;
                pixels[i + 2] = 243;
            }
        }

        var box = new Rect(10, 30, 180, 17);
        var expanded = ImageTranslateRenderer.ExpandToCoverInk(pixels, stride, w, h, box, Colors.White);

        Assert.IsTrue(expanded.Top <= 29, $"tint above not covered: {expanded.Top}");
        Assert.IsTrue(expanded.Bottom >= 48, $"tint below not covered: {expanded.Bottom}");
    }

    [TestMethod]
    public void RenderKeepsSubtleBackgroundInsideTheBox()
    {
        // Light-beige panel behind the text: the old whole-box erase turned it white,
        // the glyph mask must leave every non-ink pixel untouched.
        var result = RunOnStaThread(() =>
        {
            var source = CreateSolidBitmap(220, 90, Colors.White);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(250, 246, 238)), null, new Rect(25, 24, 145, 28));
                var ink = new SolidColorBrush(Color.FromRgb(40, 40, 40));
                for (int i = 0; i < 6; i++)
                {
                    dc.DrawRectangle(ink, null, new Rect(40 + (i * 9), 28, 4, 18));
                }
            }

            var rtb = new RenderTargetBitmap(220, 90, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();

            var line = new OcrTextLine("源", new Rect(20, 20, 150, 30), 0.9f);
            return ImageTranslateRenderer.Render(rtb, new[] { line }, new[] { "Hi" });
        });

        var pixel = ReadPixel(result, 150, 40);
        Assert.IsTrue(
            Math.Abs(pixel.R - 250) <= 6 && Math.Abs(pixel.G - 246) <= 6 && Math.Abs(pixel.B - 238) <= 6,
            $"panel pixel inside the box must survive, got {pixel}");
    }

    [TestMethod]
    public void RenderUsesSampledOriginalTextColor()
    {
        var result = RunOnStaThread(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 220, 90));
                var ink = new SolidColorBrush(Color.FromRgb(200, 40, 40));
                for (int i = 0; i < 6; i++)
                {
                    dc.DrawRectangle(ink, null, new Rect(40 + (i * 9), 28, 4, 18));
                }
            }

            var rtb = new RenderTargetBitmap(220, 90, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();

            var line = new OcrTextLine("源", new Rect(20, 20, 150, 30), 0.9f);
            return ImageTranslateRenderer.Render(rtb, new[] { line }, new[] { "Hi" });
        });

        var converted = new FormatConvertedBitmap(result, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);

        bool foundRed = false;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            if (buffer[i + 2] > 150 && buffer[i + 1] < 120 && buffer[i] < 120)
            {
                foundRed = true;
                break;
            }
        }

        Assert.IsTrue(foundRed, "translated glyphs should be painted in the sampled red ink color");
    }

    [TestMethod]
    public void RenderParagraphReflowAcrossTwoLines()
    {
        var result = RunOnStaThread(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 320, 140));
                var ink = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                for (int i = 0; i < 10; i++)
                {
                    dc.DrawRectangle(ink, null, new Rect(20 + (i * 12), 24, 5, 16));
                    dc.DrawRectangle(ink, null, new Rect(20 + (i * 12), 48, 5, 16));
                }
            }

            var rtb = new RenderTargetBitmap(320, 140, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();

            var lines = new[]
            {
                new OcrTextLine("源文一行", new Rect(15, 20, 160, 24), 0.9f),
                new OcrTextLine("源文二行", new Rect(15, 44, 160, 24), 0.9f),
            };
            var translations = new[] { "Alpha beta gamma delta", "epsilon zeta eta theta" };
            return ImageTranslateRenderer.Render(rtb, lines, translations);
        });

        Assert.IsNotNull(result);
        Assert.AreEqual(320, result.PixelWidth);
        Assert.AreEqual(140, result.PixelHeight);

        var converted = new FormatConvertedBitmap(result, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var buffer = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);

        int dark = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            if ((buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3 < 128)
            {
                dark++;
            }
        }

        Assert.IsTrue(dark > 50, $"reflowed paragraph should draw text, dark pixels={dark}");
    }

    [TestMethod]
    public void RenderKeepsSeparateRowsWhenTranslationsFit()
    {
        // Two close, overlapping UI rows: each translation fits its own box, so they
        // must stay on their own lines instead of being joined into one paragraph.
        var result = RunOnStaThread(() =>
        {
            var source = CreateSolidBitmap(320, 120, Colors.White);
            var lines = new[]
            {
                new OcrTextLine("source one", new Rect(20, 20, 200, 20), 0.9f),
                new OcrTextLine("source two", new Rect(100, 50, 100, 20), 0.9f),
            };
            return ImageTranslateRenderer.Render(source, lines, new[] { "AAAA", "BBBB" });
        });

        int darkOnSecondRow = CountDarkPixels(result, new Int32Rect(0, 52, 320, 22));
        Assert.AreNotEqual(0, darkOnSecondRow, "second row must keep its own translation");
    }

    [TestMethod]
    public void RenderCapsFontByInkHeightNotPaddedBox()
    {
        // OCR box is 34 px tall but the glyph bars are only 20 px: the translation
        // must match the ink, not grow to the padded box height.
        var result = RunOnStaThread(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 220, 90));
                var ink = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                for (int i = 0; i < 4; i++)
                {
                    dc.DrawRectangle(ink, null, new Rect(40 + (i * 12), 25, 5, 20));
                }
            }

            var rtb = new RenderTargetBitmap(220, 90, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();

            var line = new OcrTextLine("源", new Rect(20, 20, 150, 34), 0.9f);
            return ImageTranslateRenderer.Render(rtb, new[] { line }, new[] { "国" });
        });

        var bounds = DarkPixelBounds(result);
        Assert.IsTrue(bounds.HasValue, "translation must be drawn");
        Assert.IsTrue(
            bounds.Value.Height < 26,
            $"font should follow the 20 px ink height, not the 34 px box (drawn {bounds.Value.Height:0.#} px)");
    }

    private static Color ReadPixel(BitmapSource bitmap, int x, int y)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var buffer = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), buffer, 4, 0);
        return Color.FromRgb(buffer[2], buffer[1], buffer[0]);
    }

    private static int CountDarkPixels(BitmapSource bitmap, Int32Rect area, int threshold = 128)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = area.Width * 4;
        var buffer = new byte[stride * area.Height];
        converted.CopyPixels(area, buffer, stride, 0);

        int dark = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            if ((buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3 < threshold)
            {
                dark++;
            }
        }

        return dark;
    }

    private static Rect? DarkPixelBounds(BitmapSource bitmap, int threshold = 128)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int stride = width * 4;
        var buffer = new byte[stride * height];
        converted.CopyPixels(buffer, stride, 0);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * stride) + (x * 4);
                if ((buffer[i] + buffer[i + 1] + buffer[i + 2]) / 3 < threshold)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        return maxX < 0 ? null : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
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

    [TestMethod]
    public void WrapText_MissingGlyphCharacters_FallBackWithoutBreaking()
    {
        // Run on STA: FormattedText/GlyphTypeface need a Dispatcher affinity.
        Exception failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                // CJK + Latin + an astral-plane char (escaped) most faces lack:
                // the composite family fallback must keep wrapping sane - no
                // throw, no empty output, Latin words preserved.
                string text = "\u4F60\u597D\u4E16\u754C mixed with \uD83D\uDE00 emoji and Latin";
                string wrapped = ImageTranslateRenderer.WrapText(text, 120, 14);
                Assert.IsFalse(string.IsNullOrEmpty(wrapped));
                Assert.IsTrue(wrapped.Contains("mixed"), wrapped);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw failure;
        }
    }
}
