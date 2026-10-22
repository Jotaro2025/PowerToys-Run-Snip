// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Models;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// WeChat-style image translate rendering: sample background near each OCR
/// region (multi-ring median), cover the original text (axis-aligned box or
/// corner quad), pick a contrasting text color, and draw the translation with
/// wrapping + adaptive font size. Quad path uses a pure WPF affine transform — no OpenCV.
/// </summary>
public static class ImageTranslateRenderer
{
    private const double MinQuadArea = 8.0;
    private const double MinEdgeLength = 2.0;

    private static readonly Typeface Typeface = new Typeface(
        new FontFamily("Microsoft YaHei UI, Segoe UI, Arial"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    /// <summary>Outward expansion of the OCR box before erase/paint (pixels).</summary>
    internal const double BoxExpandX = 3;

    /// <summary>Outward expansion of the OCR box before erase/paint (pixels).</summary>
    internal const double BoxExpandY = 2;

    /// <summary>Inner horizontal padding so glyphs are not flush to the fill edge.</summary>
    internal const double TextPaddingX = 4;

    /// <summary>Inner vertical padding so glyphs are not flush to the fill edge.</summary>
    internal const double TextPaddingY = 3;

    /// <summary>Smallest font size used when fitting wrapped text into the box.</summary>
    internal const double MinFontSize = 8;

    /// <summary>Largest font size considered when fitting text.</summary>
    internal const double MaxFontSize = 48;

    /// <summary>Ring distances (pixels outside the box) used for background sampling.</summary>
    internal static readonly int[] SampleRingOffsets = { 1, 2, 3, 5 };

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

                    if (TryGetUsableQuad(lines[i], width, height, out var quad, out double localW, out double localH))
                    {
                        DrawQuad(dc, pixels, stride, width, height, quad, localW, localH, text);
                    }
                    else
                    {
                        DrawAxisAligned(dc, pixels, stride, width, height, lines[i].Box, text);
                    }
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

    private static void DrawAxisAligned(
        DrawingContext dc,
        byte[] pixels,
        int stride,
        int width,
        int height,
        Rect rawBox,
        string text)
    {
        var raw = Clamp(rawBox, width, height);
        if (raw.Width < 2 || raw.Height < 2)
        {
            return;
        }

        // Sample against the original OCR box so rings sit in real background.
        var bg = SampleBackground(pixels, stride, width, height, raw);
        var fg = ContrastingText(bg);

        var paintBox = ExpandBox(raw, BoxExpandX, BoxExpandY, width, height);
        dc.DrawRectangle(new SolidColorBrush(bg), null, paintBox);

        double contentWidth = Math.Max(1, paintBox.Width - (TextPaddingX * 2));
        double contentHeight = Math.Max(1, paintBox.Height - (TextPaddingY * 2));
        var fontSize = FitFontSize(text, contentWidth, contentHeight);
        var ft = CreateLaidOutText(text, fontSize, contentWidth, contentHeight, fg);

        double x = paintBox.X + TextPaddingX;
        double y = paintBox.Y + TextPaddingY + Math.Max(0, (contentHeight - ft.Height) / 2);
        dc.DrawText(ft, new Point(x, y));
    }

    private static void DrawQuad(
        DrawingContext dc,
        byte[] pixels,
        int stride,
        int width,
        int height,
        Point[] quad,
        double localW,
        double localH,
        string text)
    {
        var bounds = BoundsOf(quad);
        bounds = Clamp(bounds, width, height);
        var bg = SampleBackground(pixels, stride, width, height, bounds);
        var fg = ContrastingText(bg);

        var matrix = CreateAffineFromQuad(quad, localW, localH);
        dc.PushTransform(new MatrixTransform(matrix));
        try
        {
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, localW, localH));

            double contentWidth = Math.Max(1, localW - (TextPaddingX * 2));
            double contentHeight = Math.Max(1, localH - (TextPaddingY * 2));
            var fontSize = FitFontSize(text, contentWidth, contentHeight);
            var ft = CreateLaidOutText(text, fontSize, contentWidth, contentHeight, fg);

            double x = TextPaddingX;
            double y = TextPaddingY + Math.Max(0, (contentHeight - ft.Height) / 2);
            dc.DrawText(ft, new Point(x, y));
        }
        finally
        {
            dc.Pop();
        }
    }

    /// <summary>
    /// Returns true when <paramref name="line"/> has a usable 4-corner quad after clamping.
    /// </summary>
    internal static bool TryGetUsableQuad(
        OcrTextLine line,
        int imageWidth,
        int imageHeight,
        out Point[] quad,
        out double localWidth,
        out double localHeight)
    {
        quad = null;
        localWidth = 0;
        localHeight = 0;
        if (line?.Corners == null || line.Corners.Count < 4)
        {
            return false;
        }

        var pts = new Point[4];
        for (int i = 0; i < 4; i++)
        {
            var p = line.Corners[i];
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y))
            {
                return false;
            }

            pts[i] = new Point(
                Math.Clamp(p.X, 0, imageWidth),
                Math.Clamp(p.Y, 0, imageHeight));
        }

        if (!IsUsableQuad(pts, out localWidth, out localHeight))
        {
            return false;
        }

        quad = pts;
        return true;
    }

    /// <summary>
    /// True when four corners form a non-degenerate quad (enough area / edge length).
    /// Assumes order TL → TR → BR → BL.
    /// </summary>
    internal static bool IsUsableQuad(IReadOnlyList<Point> corners, out double localWidth, out double localHeight)
    {
        localWidth = 0;
        localHeight = 0;
        if (corners == null || corners.Count < 4)
        {
            return false;
        }

        double area = Math.Abs(Shoelace(corners));
        if (area < MinQuadArea)
        {
            return false;
        }

        localWidth = Distance(corners[0], corners[1]);
        localHeight = Distance(corners[0], corners[3]);
        if (localWidth < MinEdgeLength || localHeight < MinEdgeLength)
        {
            // Try the other pair of adjacent edges (TR→BR, TL→BL already tried height).
            double w2 = Distance(corners[3], corners[2]);
            double h2 = Distance(corners[1], corners[2]);
            localWidth = Math.Max(localWidth, w2);
            localHeight = Math.Max(localHeight, h2);
        }

        return localWidth >= MinEdgeLength && localHeight >= MinEdgeLength;
    }

    /// <summary>
    /// Affine matrix mapping local rectangle (0,0)-(w,h) onto quad corners TL/TR/BL
    /// (indices 0, 1, 3). BR is not independently constrained (affine).
    /// </summary>
    internal static Matrix CreateAffineFromQuad(IReadOnlyList<Point> quad, double localWidth, double localHeight)
    {
        ArgumentNullException.ThrowIfNull(quad);
        if (quad.Count < 4)
        {
            throw new ArgumentException("Quad needs 4 corners.", nameof(quad));
        }

        if (localWidth < MinEdgeLength || localHeight < MinEdgeLength)
        {
            throw new ArgumentException("Local size is degenerate.");
        }

        Point tl = quad[0];
        Point tr = quad[1];
        Point bl = quad[3];

        // (0,0)->TL, (w,0)->TR, (0,h)->BL
        double a = (tr.X - tl.X) / localWidth;
        double d = (tr.Y - tl.Y) / localWidth;
        double b = (bl.X - tl.X) / localHeight;
        double e = (bl.Y - tl.Y) / localHeight;
        double c = tl.X;
        double f = tl.Y;
        return new Matrix(a, d, b, e, c, f);
    }

    private static double Shoelace(IReadOnlyList<Point> pts)
    {
        double sum = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % 4];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum / 2.0;
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static Rect BoundsOf(IReadOnlyList<Point> pts)
    {
        double minX = pts[0].X, maxX = pts[0].X, minY = pts[0].Y, maxY = pts[0].Y;
        for (int i = 1; i < pts.Count; i++)
        {
            minX = Math.Min(minX, pts[i].X);
            maxX = Math.Max(maxX, pts[i].X);
            minY = Math.Min(minY, pts[i].Y);
            maxY = Math.Max(maxY, pts[i].Y);
        }

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Clamps a box to image bounds.</summary>
    internal static Rect Clamp(Rect box, int width, int height)
    {
        double x = Math.Clamp(box.X, 0, width);
        double y = Math.Clamp(box.Y, 0, height);
        double r = Math.Clamp(box.Right, 0, width);
        double b = Math.Clamp(box.Bottom, 0, height);
        return new Rect(x, y, Math.Max(0, r - x), Math.Max(0, b - y));
    }

    /// <summary>Expands a box outward then clamps to the image.</summary>
    internal static Rect ExpandBox(Rect box, double expandX, double expandY, int width, int height)
    {
        var expanded = new Rect(
            box.X - expandX,
            box.Y - expandY,
            box.Width + (expandX * 2),
            box.Height + (expandY * 2));
        return Clamp(expanded, width, height);
    }

    /// <summary>
    /// Multi-ring samples around <paramref name="box"/> and returns the per-channel median.
    /// Avoids the muddy average of mixed edge pixels on complex backgrounds.
    /// </summary>
    internal static Color SampleBackground(byte[] pixels, int stride, int width, int height, Rect box)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        var samples = new List<(byte R, byte G, byte B)>(64);

        void Sample(int px, int py)
        {
            if (px < 0 || py < 0 || px >= width || py >= height)
            {
                return;
            }

            int i = (py * stride) + (px * 4);
            samples.Add((pixels[i + 2], pixels[i + 1], pixels[i]));
        }

        int left = (int)Math.Floor(box.X);
        int top = (int)Math.Floor(box.Y);
        int right = (int)Math.Ceiling(box.Right);
        int bottom = (int)Math.Ceiling(box.Bottom);
        int boxW = Math.Max(1, right - left);
        int boxH = Math.Max(1, bottom - top);
        int stepX = Math.Max(1, boxW / 6);
        int stepY = Math.Max(1, boxH / 6);

        foreach (int ring in SampleRingOffsets)
        {
            for (int x = left; x <= right; x += stepX)
            {
                Sample(x, top - ring);
                Sample(x, bottom + ring - 1);
            }

            for (int y = top; y <= bottom; y += stepY)
            {
                Sample(left - ring, y);
                Sample(right + ring - 1, y);
            }

            Sample(left - ring, top - ring);
            Sample(right + ring - 1, top - ring);
            Sample(left - ring, bottom + ring - 1);
            Sample(right + ring - 1, bottom + ring - 1);
        }

        if (samples.Count == 0)
        {
            Sample(left + 1, top + 1);
            Sample(right - 2, top + 1);
            Sample(left + 1, bottom - 2);
            Sample(right - 2, bottom - 2);
            Sample((left + right) / 2, (top + bottom) / 2);
        }

        if (samples.Count == 0)
        {
            return Colors.White;
        }

        return MedianColor(samples);
    }

    /// <summary>Per-channel median of RGB samples.</summary>
    internal static Color MedianColor(IReadOnlyList<(byte R, byte G, byte B)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return Colors.White;
        }

        var rs = samples.Select(s => s.R).ToArray();
        var gs = samples.Select(s => s.G).ToArray();
        var bs = samples.Select(s => s.B).ToArray();
        Array.Sort(rs);
        Array.Sort(gs);
        Array.Sort(bs);
        int mid = samples.Count / 2;
        if ((samples.Count % 2) == 0)
        {
            return Color.FromRgb(
                (byte)((rs[mid - 1] + rs[mid]) / 2),
                (byte)((gs[mid - 1] + gs[mid]) / 2),
                (byte)((bs[mid - 1] + bs[mid]) / 2));
        }

        return Color.FromRgb(rs[mid], gs[mid], bs[mid]);
    }

    internal static Color ContrastingText(Color bg)
    {
        double luminance = ((0.2126 * bg.R) + (0.7152 * bg.G) + (0.0722 * bg.B)) / 255.0;
        return luminance > 0.55 ? Colors.Black : Colors.White;
    }

    /// <summary>
    /// Inserts line breaks so Latin wraps on words and CJK (no whitespace) on characters
    /// when a single line would exceed <paramref name="maxWidth"/> at <paramref name="fontSize"/>.
    /// </summary>
    internal static string WrapText(string text, double maxWidth, double fontSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || maxWidth <= 1 || fontSize < 1)
        {
            return text;
        }

        var paragraphs = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var lines = new List<string>();
        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            if (paragraph.Any(char.IsWhiteSpace))
            {
                lines.AddRange(WrapByWords(paragraph, maxWidth, fontSize));
            }
            else
            {
                lines.AddRange(WrapByCharacters(paragraph, maxWidth, fontSize));
            }
        }

        return string.Join("\n", lines);
    }

    private static List<string> WrapByWords(string paragraph, double maxWidth, double fontSize)
    {
        var tokens = TokenizeWords(paragraph);
        var lines = new List<string>();
        var current = new StringBuilder();

        void Commit()
        {
            if (current.Length == 0)
            {
                return;
            }

            lines.Add(current.ToString());
            current.Clear();
        }

        foreach (var token in tokens)
        {
            if (current.Length == 0)
            {
                if (MeasureWidth(token, fontSize) <= maxWidth)
                {
                    current.Append(token);
                }
                else
                {
                    lines.AddRange(WrapByCharacters(token, maxWidth, fontSize));
                }

                continue;
            }

            string candidate = current + token;
            if (MeasureWidth(candidate, fontSize) <= maxWidth)
            {
                current.Append(token);
                continue;
            }

            Commit();
            var next = token.TrimStart();
            if (next.Length == 0)
            {
                continue;
            }

            if (MeasureWidth(next, fontSize) <= maxWidth)
            {
                current.Append(next);
            }
            else
            {
                lines.AddRange(WrapByCharacters(next, maxWidth, fontSize));
            }
        }

        Commit();
        return lines;
    }

    private static List<string> WrapByCharacters(string text, double maxWidth, double fontSize)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        var current = new StringBuilder();
        foreach (var ch in text)
        {
            string candidate = current.ToString() + ch;
            if (current.Length > 0 && MeasureWidth(candidate, fontSize) > maxWidth)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    /// <summary>
    /// Splits into tokens where each token is either a run of non-whitespace, or a
    /// single whitespace character (so spaces stay attached for measuring).
    /// </summary>
    private static List<string> TokenizeWords(string paragraph)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        foreach (var ch in paragraph)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (word.Length > 0)
                {
                    tokens.Add(word.ToString());
                    word.Clear();
                }

                tokens.Add(ch.ToString());
            }
            else
            {
                word.Append(ch);
            }
        }

        if (word.Length > 0)
        {
            tokens.Add(word.ToString());
        }

        return tokens;
    }

    private static double MeasureWidth(string text, double fontSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var ft = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Typeface,
            fontSize,
            Brushes.Black,
            1.0);
        return ft.WidthIncludingTrailingWhitespace;
    }

    internal static double FitFontSize(string text, double maxWidth, double maxHeight)
    {
        double lo = MinFontSize;
        double hi = Math.Max(MinFontSize + 0.5, Math.Min(MaxFontSize, maxHeight));
        double best = MinFontSize;

        for (int i = 0; i < 12; i++)
        {
            double mid = (lo + hi) / 2;
            var ft = CreateLaidOutText(text, mid, maxWidth, maxHeight, Colors.Black);
            if (ft.Width <= maxWidth + 0.5 && ft.Height <= maxHeight + 0.5)
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

    private static FormattedText CreateLaidOutText(
        string text,
        double fontSize,
        double maxWidth,
        double maxHeight,
        Color foreground)
    {
        var display = WrapText(text, maxWidth, fontSize);
        return new FormattedText(
            display,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Typeface,
            fontSize,
            new SolidColorBrush(foreground),
            1.0)
        {
            MaxTextWidth = maxWidth,
            MaxTextHeight = maxHeight,
            Trimming = TextTrimming.None,
        };
    }
}
