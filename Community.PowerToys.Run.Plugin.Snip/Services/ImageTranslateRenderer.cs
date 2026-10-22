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
/// region (multi-ring median for text color), erase original text with a pure-C#
/// Telea-like inpaint on <c>WriteableBitmap.BackBuffer</c> via <see cref="Span{T}"/>
/// (median fill fallback; no full-frame <c>byte[]</c> Marshal.Copy), then draw the translation with
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

        // Work in Bgra32 so we can sample / inpaint cheaply.
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var writable = new WriteableBitmap(converted);
        var jobs = new List<DrawJob>(lines.Count);

        // Inpaint directly on BackBuffer (Span) — no full-frame byte[] + double Marshal.Copy.
        // Unlock before DrawingVisual so Lock/Unlock only covers pixel mutation.
        writable.Lock();
        try
        {
            int stride = writable.BackBufferStride;
            int byteCount = checked(stride * height);
            Span<byte> pixels;
            unsafe
            {
                pixels = new Span<byte>((void*)writable.BackBuffer, byteCount);
            }

            // Pass 1: sample contrast colors from the *original* buffer, then erase
            // each OCR region via Telea-like inpaint (median solid fill as fallback).
            // Dirty only the erased boxes (not the whole frame).
            for (int i = 0; i < lines.Count; i++)
            {
                var text = translations[i];
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (TryGetUsableQuad(lines[i], width, height, out var quad, out double localW, out double localH))
                {
                    var bounds = Clamp(BoundsOf(quad), width, height);
                    if (bounds.Width < 2 || bounds.Height < 2)
                    {
                        continue;
                    }

                    var bg = SampleBackground(pixels, stride, width, height, bounds);
                    EraseRegion(pixels, stride, width, height, bounds, bg);
                    AddDirtyRegion(writable, bounds, width, height);
                    jobs.Add(DrawJob.ForQuad(quad, localW, localH, text, ContrastingText(bg)));
                }
                else
                {
                    var raw = Clamp(lines[i].Box, width, height);
                    if (raw.Width < 2 || raw.Height < 2)
                    {
                        continue;
                    }

                    var bg = SampleBackground(pixels, stride, width, height, raw);
                    var paintBox = ExpandBox(raw, BoxExpandX, BoxExpandY, width, height);
                    EraseRegion(pixels, stride, width, height, paintBox, bg);
                    AddDirtyRegion(writable, paintBox, width, height);
                    jobs.Add(DrawJob.ForAxis(paintBox, text, ContrastingText(bg)));
                }
            }
        }
        finally
        {
            writable.Unlock();
        }

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(writable, new Rect(0, 0, width, height));

            foreach (var job in jobs)
            {
                if (job.IsQuad)
                {
                    DrawQuadText(dc, job.Quad, job.LocalWidth, job.LocalHeight, job.Text, job.Foreground);
                }
                else
                {
                    DrawAxisText(dc, job.PaintBox, job.Text, job.Foreground);
                }
            }
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// Telea-like inpaint into <paramref name="pixels"/> (typically a BackBuffer
    /// <see cref="Span{T}"/>); falls back to solid median fill for tiny boxes or
    /// when propagation fails.
    /// </summary>
    internal static bool EraseRegion(
        Span<byte> pixels,
        int stride,
        int width,
        int height,
        Rect region,
        Color medianFallback)
    {
        int x0 = (int)Math.Floor(region.X);
        int y0 = (int)Math.Floor(region.Y);
        int x1 = (int)Math.Ceiling(region.Right);
        int y1 = (int)Math.Ceiling(region.Bottom);
        x0 = Math.Clamp(x0, 0, width);
        y0 = Math.Clamp(y0, 0, height);
        x1 = Math.Clamp(x1, 0, width);
        y1 = Math.Clamp(y1, 0, height);

        if (LightInpaint.TryInpaintRect(pixels, stride, width, height, x0, y0, x1, y1))
        {
            return true;
        }

        LightInpaint.FillRectSolid(
            pixels,
            stride,
            width,
            height,
            x0,
            y0,
            x1,
            y1,
            medianFallback.B,
            medianFallback.G,
            medianFallback.R);
        return false;
    }

    /// <summary>Marks only the OCR erase box dirty (not the full bitmap).</summary>
    private static void AddDirtyRegion(WriteableBitmap bitmap, Rect region, int width, int height)
    {
        int x0 = Math.Clamp((int)Math.Floor(region.X), 0, width);
        int y0 = Math.Clamp((int)Math.Floor(region.Y), 0, height);
        int x1 = Math.Clamp((int)Math.Ceiling(region.Right), 0, width);
        int y1 = Math.Clamp((int)Math.Ceiling(region.Bottom), 0, height);
        if (x1 > x0 && y1 > y0)
        {
            bitmap.AddDirtyRect(new Int32Rect(x0, y0, x1 - x0, y1 - y0));
        }
    }

    private static void DrawAxisText(DrawingContext dc, Rect paintBox, string text, Color fg)
    {
        double contentWidth = Math.Max(1, paintBox.Width - (TextPaddingX * 2));
        double contentHeight = Math.Max(1, paintBox.Height - (TextPaddingY * 2));
        var cache = new TextMeasureCache();
        var fontSize = FitFontSize(text, contentWidth, contentHeight, cache);
        var ft = CreateLaidOutText(text, fontSize, contentWidth, contentHeight, fg, cache);

        double x = paintBox.X + TextPaddingX;
        double y = paintBox.Y + TextPaddingY + Math.Max(0, (contentHeight - ft.Height) / 2);
        dc.DrawText(ft, new Point(x, y));
    }

    private static void DrawQuadText(
        DrawingContext dc,
        Point[] quad,
        double localW,
        double localH,
        string text,
        Color fg)
    {
        var matrix = CreateAffineFromQuad(quad, localW, localH);
        dc.PushTransform(new MatrixTransform(matrix));
        try
        {
            double contentWidth = Math.Max(1, localW - (TextPaddingX * 2));
            double contentHeight = Math.Max(1, localH - (TextPaddingY * 2));
            var cache = new TextMeasureCache();
            var fontSize = FitFontSize(text, contentWidth, contentHeight, cache);
            var ft = CreateLaidOutText(text, fontSize, contentWidth, contentHeight, fg, cache);

            double x = TextPaddingX;
            double y = TextPaddingY + Math.Max(0, (contentHeight - ft.Height) / 2);
            dc.DrawText(ft, new Point(x, y));
        }
        finally
        {
            dc.Pop();
        }
    }

    private readonly struct DrawJob
    {
        private DrawJob(Rect paintBox, Point[] quad, double localW, double localH, string text, Color fg, bool isQuad)
        {
            PaintBox = paintBox;
            Quad = quad;
            LocalWidth = localW;
            LocalHeight = localH;
            Text = text;
            Foreground = fg;
            IsQuad = isQuad;
        }

        public Rect PaintBox { get; }

        public Point[] Quad { get; }

        public double LocalWidth { get; }

        public double LocalHeight { get; }

        public string Text { get; }

        public Color Foreground { get; }

        public bool IsQuad { get; }

        public static DrawJob ForAxis(Rect paintBox, string text, Color fg)
            => new DrawJob(paintBox, null, 0, 0, text, fg, isQuad: false);

        public static DrawJob ForQuad(Point[] quad, double localW, double localH, string text, Color fg)
            => new DrawJob(default, quad, localW, localH, text, fg, isQuad: true);
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
    internal static Color SampleBackground(ReadOnlySpan<byte> pixels, int stride, int width, int height, Rect box)
    {
        var samples = new List<(byte R, byte G, byte B)>(64);

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
                TryAddBackgroundSample(pixels, samples, stride, width, height, x, top - ring);
                TryAddBackgroundSample(pixels, samples, stride, width, height, x, bottom + ring - 1);
            }

            for (int y = top; y <= bottom; y += stepY)
            {
                TryAddBackgroundSample(pixels, samples, stride, width, height, left - ring, y);
                TryAddBackgroundSample(pixels, samples, stride, width, height, right + ring - 1, y);
            }

            TryAddBackgroundSample(pixels, samples, stride, width, height, left - ring, top - ring);
            TryAddBackgroundSample(pixels, samples, stride, width, height, right + ring - 1, top - ring);
            TryAddBackgroundSample(pixels, samples, stride, width, height, left - ring, bottom + ring - 1);
            TryAddBackgroundSample(pixels, samples, stride, width, height, right + ring - 1, bottom + ring - 1);
        }

        if (samples.Count == 0)
        {
            TryAddBackgroundSample(pixels, samples, stride, width, height, left + 1, top + 1);
            TryAddBackgroundSample(pixels, samples, stride, width, height, right - 2, top + 1);
            TryAddBackgroundSample(pixels, samples, stride, width, height, left + 1, bottom - 2);
            TryAddBackgroundSample(pixels, samples, stride, width, height, right - 2, bottom - 2);
            TryAddBackgroundSample(pixels, samples, stride, width, height, (left + right) / 2, (top + bottom) / 2);
        }

        if (samples.Count == 0)
        {
            return Colors.White;
        }

        return MedianColor(samples);
    }

    private static void TryAddBackgroundSample(
        ReadOnlySpan<byte> pixels,
        List<(byte R, byte G, byte B)> samples,
        int stride,
        int width,
        int height,
        int px,
        int py)
    {
        if (px < 0 || py < 0 || px >= width || py >= height)
        {
            return;
        }

        int i = (py * stride) + (px * 4);
        samples.Add((pixels[i + 2], pixels[i + 1], pixels[i]));
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

        return WrapTextCore(text, maxWidth, fontSize, new TextMeasureCache());
    }

    private static string WrapTextCore(string text, double maxWidth, double fontSize, TextMeasureCache cache)
    {
        var lines = new List<string>();
        foreach (var paragraphSpan in text.AsSpan().EnumerateLines())
        {
            if (paragraphSpan.IsEmpty)
            {
                lines.Add(string.Empty);
                continue;
            }

            string paragraph = paragraphSpan.ToString();
            if (ContainsWhiteSpace(paragraphSpan))
            {
                lines.AddRange(WrapByWords(paragraph, maxWidth, fontSize, cache));
            }
            else
            {
                lines.AddRange(WrapByCharacters(paragraph, maxWidth, fontSize, cache));
            }
        }

        return string.Join("\n", lines);
    }

    private static bool ContainsWhiteSpace(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> WrapByWords(string paragraph, double maxWidth, double fontSize, TextMeasureCache cache)
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
                if (cache.MeasureWidth(token, fontSize) <= maxWidth)
                {
                    current.Append(token);
                }
                else
                {
                    lines.AddRange(WrapByCharacters(token, maxWidth, fontSize, cache));
                }

                continue;
            }

            string candidate = current + token;
            if (cache.MeasureWidth(candidate, fontSize) <= maxWidth)
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

            if (cache.MeasureWidth(next, fontSize) <= maxWidth)
            {
                current.Append(next);
            }
            else
            {
                lines.AddRange(WrapByCharacters(next, maxWidth, fontSize, cache));
            }
        }

        Commit();
        return lines;
    }

    private static List<string> WrapByCharacters(string text, double maxWidth, double fontSize, TextMeasureCache cache)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        var current = new StringBuilder();
        double currentWidth = 0;
        foreach (var ch in text)
        {
            double chWidth = cache.MeasureCharWidth(ch, fontSize);
            if (current.Length > 0 && (currentWidth + chWidth) > maxWidth)
            {
                lines.Add(current.ToString());
                current.Clear();
                currentWidth = 0;
            }

            current.Append(ch);
            currentWidth += chWidth;
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

    internal static double FitFontSize(string text, double maxWidth, double maxHeight)
        => FitFontSize(text, maxWidth, maxHeight, new TextMeasureCache());

    private static double FitFontSize(string text, double maxWidth, double maxHeight, TextMeasureCache cache)
    {
        double lo = MinFontSize;
        double hi = Math.Max(MinFontSize + 0.5, Math.Min(MaxFontSize, maxHeight));
        double best = MinFontSize;

        // One cache shared across binary-search iterations + wrap measurements.
        for (int i = 0; i < 12; i++)
        {
            double mid = (lo + hi) / 2;
            var ft = CreateLaidOutText(text, mid, maxWidth, maxHeight, Colors.Black, cache);
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
        Color foreground,
        TextMeasureCache cache)
    {
        var display = WrapTextCore(text, maxWidth, fontSize, cache);
        Brush brush = foreground == Colors.Black ? Brushes.Black : new SolidColorBrush(foreground);
        return new FormattedText(
            display,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            Typeface,
            fontSize,
            brush,
            1.0)
        {
            MaxTextWidth = maxWidth,
            MaxTextHeight = maxHeight,
            Trimming = TextTrimming.None,
        };
    }

    /// <summary>
    /// Per fit/wrap pass cache of FormattedText widths (and GlyphTypeface advances
    /// for CJK character peeling) so FitFontSize / WrapText do not rebuild metrics
    /// for the same string×size repeatedly.
    /// </summary>
    private sealed class TextMeasureCache
    {
        private readonly Dictionary<(string Text, int SizeKey), double> _stringWidths = new();
        private readonly Dictionary<(int Char, int SizeKey), double> _charWidths = new();
        private GlyphTypeface _glyphTypeface;
        private bool _glyphResolved;

        public double MeasureWidth(string text, double fontSize)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            int sizeKey = SizeKey(fontSize);
            var key = (text, sizeKey);
            if (_stringWidths.TryGetValue(key, out double cached))
            {
                return cached;
            }

            var ft = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                Typeface,
                fontSize,
                Brushes.Black,
                1.0);
            double width = ft.WidthIncludingTrailingWhitespace;
            _stringWidths[key] = width;
            return width;
        }

        /// <summary>
        /// Prefer GlyphTypeface advance (CJK-safe, no FormattedText alloc); fall back
        /// to a single-char FormattedText cached by codepoint×size.
        /// </summary>
        public double MeasureCharWidth(char ch, double fontSize)
        {
            int sizeKey = SizeKey(fontSize);
            var key = ((int)ch, sizeKey);
            if (_charWidths.TryGetValue(key, out double cached))
            {
                return cached;
            }

            double width;
            if (TryGetGlyphTypeface(out GlyphTypeface glyph)
                && glyph.CharacterToGlyphMap.TryGetValue(ch, out ushort glyphIndex))
            {
                width = glyph.AdvanceWidths[glyphIndex] * fontSize;
            }
            else
            {
                width = MeasureWidth(ch.ToString(), fontSize);
            }

            _charWidths[key] = width;
            return width;
        }

        private bool TryGetGlyphTypeface(out GlyphTypeface glyph)
        {
            if (!_glyphResolved)
            {
                _glyphResolved = true;
                _ = Typeface.TryGetGlyphTypeface(out _glyphTypeface);
            }

            glyph = _glyphTypeface;
            return glyph != null;
        }

        private static int SizeKey(double fontSize)
            => (int)Math.Round(fontSize * 100.0);
    }
}
