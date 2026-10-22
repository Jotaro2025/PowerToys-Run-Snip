// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Models;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// WeChat-style v1 image translate rendering: sample background near each OCR
/// box, fill a rectangle to cover the original text, pick a contrasting text
/// color, and draw the translation with an adaptive font size.
/// </summary>
public static class ImageTranslateRenderer
{
    private static readonly Typeface Typeface = new Typeface(
        new FontFamily("Microsoft YaHei UI, Segoe UI, Arial"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    /// <summary>
    /// Paints translations over <paramref name="source"/> and returns a new frozen bitmap.
    /// </summary>
    public static BitmapSource Render(
        BitmapSource source,
        IReadOnlyList<OcrTextLine> lines,
        IReadOnlyList<string> translations)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(translations);
        if (lines.Count != translations.Count)
        {
            throw new ArgumentException("lines and translations must have the same count.");
        }

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return source;
        }

        // Work in Bgra32 so we can sample background colors cheaply.
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var writable = new WriteableBitmap(converted);
        writable.Lock();
        try
        {
            var stride = writable.BackBufferStride;
            var pixels = new byte[stride * height];
            System.Runtime.InteropServices.Marshal.Copy(writable.BackBuffer, pixels, 0, pixels.Length);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawImage(writable, new Rect(0, 0, width, height));

                for (int i = 0; i < lines.Count; i++)
                {
                    var text = translations[i];
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var box = Clamp(lines[i].Box, width, height);
                    if (box.Width < 2 || box.Height < 2)
                    {
                        continue;
                    }

                    var bg = SampleBackground(pixels, stride, width, height, box);
                    var fg = ContrastingText(bg);
                    dc.DrawRectangle(new SolidColorBrush(bg), null, box);

                    var fontSize = FitFontSize(text, box.Width - 2, box.Height - 2);
                    var ft = new FormattedText(
                        text,
                        CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        Typeface,
                        fontSize,
                        new SolidColorBrush(fg),
                        1.0);

                    // Center vertically within the box; slight left padding.
                    double x = box.X + 1;
                    double y = box.Y + Math.Max(0, (box.Height - ft.Height) / 2);
                    dc.DrawText(ft, new Point(x, y));
                }
            }

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        finally
        {
            writable.Unlock();
        }
    }

    private static Rect Clamp(Rect box, int width, int height)
    {
        double x = Math.Clamp(box.X, 0, width);
        double y = Math.Clamp(box.Y, 0, height);
        double r = Math.Clamp(box.Right, 0, width);
        double b = Math.Clamp(box.Bottom, 0, height);
        return new Rect(x, y, Math.Max(0, r - x), Math.Max(0, b - y));
    }

    private static Color SampleBackground(byte[] pixels, int stride, int width, int height, Rect box)
    {
        // Sample a few pixels just outside the box (and a couple inside as fallback).
        long sumB = 0, sumG = 0, sumR = 0;
        int count = 0;

        void Sample(int px, int py)
        {
            if (px < 0 || py < 0 || px >= width || py >= height)
            {
                return;
            }

            int i = (py * stride) + (px * 4);
            sumB += pixels[i];
            sumG += pixels[i + 1];
            sumR += pixels[i + 2];
            count++;
        }

        int left = (int)Math.Floor(box.X);
        int top = (int)Math.Floor(box.Y);
        int right = (int)Math.Ceiling(box.Right);
        int bottom = (int)Math.Ceiling(box.Bottom);

        for (int x = left; x < right; x += Math.Max(1, (right - left) / 4))
        {
            Sample(x, top - 2);
            Sample(x, bottom + 1);
        }

        for (int y = top; y < bottom; y += Math.Max(1, (bottom - top) / 4))
        {
            Sample(left - 2, y);
            Sample(right + 1, y);
        }

        if (count == 0)
        {
            Sample((left + right) / 2, (top + bottom) / 2);
        }

        if (count == 0)
        {
            return Colors.White;
        }

        return Color.FromRgb(
            (byte)(sumR / count),
            (byte)(sumG / count),
            (byte)(sumB / count));
    }

    private static Color ContrastingText(Color bg)
    {
        // Relative luminance (sRGB approx).
        double luminance = ((0.2126 * bg.R) + (0.7152 * bg.G) + (0.0722 * bg.B)) / 255.0;
        return luminance > 0.55 ? Colors.Black : Colors.White;
    }

    private static double FitFontSize(string text, double maxWidth, double maxHeight)
    {
        double lo = 6;
        double hi = Math.Max(7, Math.Min(48, maxHeight));
        double best = lo;

        for (int i = 0; i < 10; i++)
        {
            double mid = (lo + hi) / 2;
            var ft = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                Typeface,
                mid,
                Brushes.Black,
                1.0);

            if (ft.Width <= maxWidth && ft.Height <= maxHeight)
            {
                best = mid;
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return best;
    }
}
