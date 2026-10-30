// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class TextMaskAnalyzerTests
{
    private const int Width = 220;
    private const int Height = 90;

    [TestMethod]
    public void FlatBackground_ExtractsGlyphMaskAndInkColor()
    {
        var pixels = Fill(Width, Height, 255, 255, 255);
        PaintGlyphBars(pixels, x0: 28, y0: 24, bars: 6, barWidth: 4, barHeight: 20, r: 30, g: 30, b: 30);

        var result = TextMaskAnalyzer.TryBuild(pixels, Width * 4, Width, Height, new Rect(20, 20, 150, 30));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsUsable, "flat background with glyph bars must produce a usable mask");
        Assert.AreEqual(MaskFillKind.Plane, result.FillKind);
        Assert.IsTrue(result.Background.R > 240, $"background should stay white, got {result.Background}");
        Assert.IsTrue(result.Foreground.R < 80 && result.Foreground.G < 80 && result.Foreground.B < 80, $"ink should stay dark, got {result.Foreground}");
        Assert.AreEqual(1, MaskAt(result, 28, 30), "bar pixel must be masked");
        Assert.AreEqual(0, MaskAt(result, 33, 30), "background pixel between bars must stay untouched");
        Assert.AreEqual(0, MaskAt(result, 21, 21), "box corner background must stay untouched");
        Assert.IsTrue(result.Bounds.Width >= 40, $"mask bounds should span the bars, got {result.Bounds}");
    }

    [TestMethod]
    public void DarkTheme_KeepsWhiteInk()
    {
        var pixels = Fill(Width, Height, 24, 24, 28);
        PaintGlyphBars(pixels, x0: 28, y0: 24, bars: 6, barWidth: 4, barHeight: 20, r: 245, g: 245, b: 245);

        var result = TextMaskAnalyzer.TryBuild(pixels, Width * 4, Width, Height, new Rect(20, 20, 150, 30));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsUsable);
        Assert.IsTrue(result.Foreground.R > 200 && result.Foreground.G > 200, $"white text on dark must stay white, got {result.Foreground}");
        Assert.IsTrue(result.Background.R < 80, $"dark background must stay dark, got {result.Background}");
        Assert.AreEqual(1, MaskAt(result, 28, 30));
    }

    [TestMethod]
    public void ColoredText_IsPreservedInsteadOfBlackWhite()
    {
        var pixels = Fill(Width, Height, 255, 255, 255);
        PaintGlyphBars(pixels, x0: 28, y0: 24, bars: 6, barWidth: 5, barHeight: 20, r: 220, g: 30, b: 30);

        var result = TextMaskAnalyzer.TryBuild(pixels, Width * 4, Width, Height, new Rect(20, 20, 150, 30));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsUsable);
        Assert.IsTrue(
            result.Foreground.R > 170 && result.Foreground.G < 100 && result.Foreground.B < 100,
            $"red text must keep its color, got {result.Foreground}");
    }

    [TestMethod]
    public void PlateBackground_UsesPlateColorAndKeepsPlatePixels()
    {
        // Page white, light-grey pill slightly larger than the box, dark glyphs inside.
        var pixels = Fill(Width, Height, 255, 255, 255);
        FillRect(pixels, 16, 16, 184, 64, 220, 220, 220);
        PaintGlyphBars(pixels, x0: 40, y0: 30, bars: 5, barWidth: 5, barHeight: 18, r: 40, g: 40, b: 40);

        var result = TextMaskAnalyzer.TryBuild(pixels, Width * 4, Width, Height, new Rect(20, 24, 160, 32));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsUsable);
        Assert.IsTrue(
            Math.Abs(result.Background.R - 220) <= 4,
            $"local background should be the pill colour, got {result.Background}");
        Assert.AreEqual(MaskFillKind.Solid, result.FillKind);
        Assert.AreEqual(1, MaskAt(result, 40, 36), "glyph pixel must be masked");
        Assert.AreEqual(0, MaskAt(result, 25, 27), "pill pixel without ink must stay untouched");
    }

    [TestMethod]
    public void GradientBackground_FitsPlaneAndKeepsInk()
    {
        var pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte v = (byte)Math.Clamp(x * 255 / (Width - 1), 0, 255);
                int i = ((y * Width) + x) * 4;
                pixels[i] = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
        }

        PaintGlyphBars(pixels, x0: 45, y0: 25, bars: 7, barWidth: 4, barHeight: 20, r: 10, g: 10, b: 10);

        var result = TextMaskAnalyzer.TryBuild(pixels, Width * 4, Width, Height, new Rect(40, 20, 140, 30));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.IsUsable);
        Assert.AreEqual(MaskFillKind.Plane, result.FillKind);
        Assert.AreEqual(1, MaskAt(result, 45, 30), "glyph over the dark end must be masked");
        Assert.AreEqual(1, MaskAt(result, 45 + (6 * 8), 30), "glyph over the bright end must be masked");
        Assert.AreEqual(0, MaskAt(result, 50, 22), "gradient background must not be masked");
    }

    [TestMethod]
    public void NoiseBackground_FallsBackToInpaintOrRejects()
    {
        var random = new Random(7);
        var pixels = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte v = (byte)Math.Clamp(120 + random.Next(-60, 61), 0, 255);
                int i = ((y * Width) + x) * 4;
                pixels[i] = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
        }

        PaintGlyphBars(pixels, x0: 45, y0: 25, bars: 7, barWidth: 6, barHeight: 20, r: 0, g: 0, b: 0);

        var result = TextMaskAnalyzer.TryBuild(pixels, Width * 4, Width, Height, new Rect(40, 20, 140, 30));

        Assert.IsTrue(
            result == null || !result.IsUsable || result.FillKind == MaskFillKind.Inpaint,
            "texture backgrounds must not be reconstructed with a flat plane fill");
    }

    [TestMethod]
    public void NoInk_IsNotUsable()
    {
        var pixels = Fill(Width, Height, 255, 255, 255);
        var result = TextMaskAnalyzer.TryBuild(pixels, Width * 4, Width, Height, new Rect(20, 20, 150, 30));

        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsUsable, "an empty box must not produce a glyph mask");
        Assert.AreEqual(0, result.MaskPixelCount);
    }

    [TestMethod]
    public void TinyBox_ReturnsNull()
    {
        var pixels = Fill(Width, Height, 255, 255, 255);
        Assert.IsNull(TextMaskAnalyzer.TryBuild(pixels, Width * 4, Width, Height, new Rect(20, 20, 4, 4)));
    }

    private static int MaskAt(TextMaskResult result, int x, int y)
    {
        int localX = x - result.Left;
        int localY = y - result.Top;
        Assert.IsTrue(localX >= 0 && localX < result.Width && localY >= 0 && localY < result.Height);
        return result.Mask[(localY * result.Width) + localX];
    }

    private static byte[] Fill(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    private static void FillRect(byte[] pixels, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        int stride = Width * 4;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * stride) + (x * 4);
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 255;
            }
        }
    }

    private static void PaintGlyphBars(
        byte[] pixels,
        int x0,
        int y0,
        int bars,
        int barWidth,
        int barHeight,
        byte r,
        byte g,
        byte b)
    {
        int stride = Width * 4;
        for (int bar = 0; bar < bars; bar++)
        {
            int left = x0 + (bar * (barWidth + 4));
            for (int y = y0; y < y0 + barHeight; y++)
            {
                for (int x = left; x < left + barWidth; x++)
                {
                    int i = (y * stride) + (x * 4);
                    pixels[i] = b;
                    pixels[i + 1] = g;
                    pixels[i + 2] = r;
                    pixels[i + 3] = 255;
                }
            }
        }
    }
}
