// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
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
/// WeChat-style image translate rendering. For each OCR region it builds a glyph mask
/// (local background plane / interior median), erases only the glyph pixels (Telea-like
/// inpaint or exact plane fill), then paints the translation in the sampled original
/// color with wrapping + adaptive font size. Axis-aligned lines are grouped into
/// paragraphs so long translations reflow instead of shrinking per line. Quad path uses
/// a pure WPF affine transform — no OpenCV.
/// </summary>
public static class ImageTranslateRenderer
{
    private const double MinQuadArea = 8.0;
    private const double MinEdgeLength = 2.0;

    private static readonly Typeface SemiBoldTypeface = new Typeface(
        new FontFamily("Microsoft YaHei UI, Segoe UI, Arial"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    private static readonly Typeface BoldTypeface = new Typeface(
        new FontFamily("Microsoft YaHei UI, Segoe UI, Arial"),
        FontStyles.Normal,
        FontWeights.Bold,
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

    /// <summary>
    /// Extra height the erase/paint box may grow by when the translation needs more
    /// lines than the OCR box had, so long translations are not silently dropped.
    /// </summary>
    internal const double MaxBoxGrowth = 40;

    /// <summary>Luminance distance from the sampled background that counts as ink.</summary>
    internal const int InkDelta = 40;

    /// <summary>Luminance distance that counts as a flat tint (inline-code background).</summary>
    internal const int TintDelta = 8;

    /// <summary>Rows a flat tint may add on top of the ink expansion, per side.</summary>
    internal const int TintRowBudget = 2;

    /// <summary>Fraction of the box width tinted before a row counts as a background strip.</summary>
    internal const double MinTintFraction = 0.05;

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

            // Group visual lines into paragraphs: a long translation reflows over the
            // paragraph's union box instead of being squeezed into one line box.
            var paragraphs = TranslateParagraph.Group(lines);
            foreach (var paragraph in paragraphs)
            {
                if (paragraph.IsSingleLine)
                {
                    RenderSingleLine(
                        pixels,
                        stride,
                        width,
                        height,
                        writable,
                        lines,
                        translations,
                        paragraph.LineIndices[0],
                        jobs);
                }
                else
                {
                    RenderParagraph(
                        pixels,
                        stride,
                        width,
                        height,
                        writable,
                        lines,
                        translations,
                        paragraph,
                        jobs);
                }
            }
        }
        finally
        {
            writable.Unlock();
        }

        // Composite glyphs into the same WriteableBitmap via per-region RTBs
        // (no second full-frame RenderTargetBitmap — cuts peak ~1× frame).
        foreach (var job in jobs)
        {
            switch (job.Kind)
            {
                case DrawKind.Quad:
                    BlitQuadText(writable, job);
                    break;
                case DrawKind.Paragraph:
                    BlitParagraphText(writable, job);
                    break;
                default:
                    BlitAxisText(writable, job);
                    break;
            }
        }

        if (writable.CanFreeze)
        {
            writable.Freeze();
        }

        return writable;
    }

    private static void RenderSingleLine(
        Span<byte> pixels,
        int stride,
        int width,
        int height,
        WriteableBitmap dest,
        IReadOnlyList<OcrTextLine> lines,
        IReadOnlyList<string> translations,
        int index,
        List<DrawJob> jobs)
    {
        var line = lines[index];
        var text = translations[index];
        if (line == null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (TryGetUsableQuad(line, width, height, out var quad, out double localW, out double localH))
        {
            var bounds = Clamp(BoundsOf(quad), width, height);
            if (bounds.Width < 2 || bounds.Height < 2)
            {
                return;
            }

            var eraseBox = ExpandBox(bounds, 1, 1, width, height);
            var style = EraseSmart(pixels, stride, width, height, eraseBox, dest);
            double cap = FontCap(new Rect(0, 0, localW, localH), style.InkHeight);
            var quadLayout = BuildTextLayout(
                text,
                Math.Max(1, localW - (TextPaddingX * 2)),
                Math.Max(1, localH - (TextPaddingY * 2)),
                cap,
                style.Bold);
            jobs.Add(DrawJob.ForQuad(quad, localW, localH, quadLayout, style.Foreground, style.Bold));
            return;
        }

        var raw = Clamp(line.Box, width, height);
        if (raw.Width < 2 || raw.Height < 2)
        {
            return;
        }

        // Sample the surround before the box grows: the extra rows the translation
        // needs may already hold neighbouring text.
        var bg = SampleBackground(pixels, stride, width, height, raw);
        var paintBox = ExpandBox(raw, BoxExpandX, BoxExpandY, width, height);
        paintBox = ExpandToCoverInk(pixels, stride, width, height, paintBox, bg);
        var textStyle = EraseSmart(pixels, stride, width, height, paintBox, dest);

        // Font size is capped by the original line height, so growing the box only
        // buys wrapped lines, never an inflated font.
        double contentWidth = Math.Max(1, paintBox.Width - (TextPaddingX * 2));
        double fontCap = FontCap(raw, textStyle.InkHeight);
        var layout = BuildTextLayout(
            text,
            contentWidth,
            Math.Max(1, paintBox.Height - (TextPaddingY * 2)),
            fontCap,
            textStyle.Bold);

        var grown = GrowBoxToFit(paintBox, layout.Size.Height + (TextPaddingY * 2), width, height);
        if (grown.Height > paintBox.Height + 0.5)
        {
            // Re-fit after growth: the extra height may allow a larger (but still
            // capped) font, and the layout must match the box we actually draw into.
            paintBox = grown;
            layout = BuildTextLayout(
                text,
                contentWidth,
                Math.Max(1, paintBox.Height - (TextPaddingY * 2)),
                fontCap,
                textStyle.Bold);
        }

        jobs.Add(DrawJob.ForAxis(paintBox, raw, layout, textStyle.Foreground, textStyle.Bold));
    }

    /// <summary>
    /// Font-size ceiling for one source line. The OCR box includes leading/padding, so
    /// its height overestimates the original font; the glyph-mask height
    /// (ascender→descender) tracks the real size much more closely. The box cap still
    /// bounds the result, and lines without a usable mask keep the old box-based cap.
    /// </summary>
    private static double FontCap(Rect raw, double inkHeight)
    {
        double boxCap = Math.Max(MinFontSize + 0.5, raw.Height - (TextPaddingY * 2));
        if (inkHeight <= 1)
        {
            return boxCap;
        }

        double inkCap = Math.Max(MinFontSize + 0.5, inkHeight * 1.05);
        return Math.Min(boxCap, inkCap);
    }

    private static void RenderParagraph(
        Span<byte> pixels,
        int stride,
        int width,
        int height,
        WriteableBitmap dest,
        IReadOnlyList<OcrTextLine> lines,
        IReadOnlyList<string> translations,
        TranslateParagraph paragraph,
        List<DrawJob> jobs)
    {
        TextStyle style = default;
        bool hasStyle = false;
        var entries = new List<ParagraphLine>(paragraph.LineIndices.Count);

        foreach (int index in paragraph.LineIndices)
        {
            var text = translations[index];
            if (string.IsNullOrWhiteSpace(text))
            {
                // Do not erase a source line that has no replacement to paint.
                continue;
            }

            var raw = Clamp(lines[index].Box, width, height);
            if (raw.Width < 2 || raw.Height < 2)
            {
                continue;
            }

            var bg = SampleBackground(pixels, stride, width, height, raw);
            var paintBox = ExpandToCoverInk(
                pixels,
                stride,
                width,
                height,
                ExpandBox(raw, BoxExpandX, BoxExpandY, width, height),
                bg);

            var lineStyle = EraseSmart(pixels, stride, width, height, paintBox, dest);
            if (!hasStyle || lineStyle.InkPixels > style.InkPixels)
            {
                style = lineStyle;
                hasStyle = true;
            }

            entries.Add(new ParagraphLine(raw, paintBox, lineStyle, text));
        }

        if (!hasStyle || entries.Count == 0)
        {
            return;
        }

        // Separate UI rows can sit close enough to look like one paragraph. When every
        // translation still fits its own line box at a readable size, keep the original
        // line structure; only reflow when a translation genuinely does not fit.
        if (TryRenderPerLine(entries, width, height, jobs))
        {
            return;
        }

        string joined = JoinTranslationParts(entries.Select(e => e.Text).ToList());
        double top = paragraph.Box.Top;
        double maxHeight = AvailableHeight(lines, paragraph, height);

        double contentWidth = Math.Max(1, paragraph.Box.Width - (TextPaddingX * 2));
        double fontCap = FontCap(entries[0].Raw, entries[0].Style.InkHeight);
        for (int i = 1; i < entries.Count; i++)
        {
            fontCap = Math.Min(fontCap, FontCap(entries[i].Raw, entries[i].Style.InkHeight));
        }

        var layout = BuildTextLayout(
            joined,
            contentWidth,
            Math.Max(1, maxHeight - (TextPaddingY * 2)),
            fontCap,
            style.Bold);

        double blockHeight = Math.Min(
            Math.Max(1, maxHeight),
            Math.Max(paragraph.Box.Height, layout.Size.Height + (TextPaddingY * 2)));
        var block = new Rect(paragraph.Box.X, top, paragraph.Box.Width, blockHeight);

        var first = Clamp(lines[paragraph.LineIndices[0]].Box, width, height);
        var alignment = paragraph.Alignment switch
        {
            ParagraphAlignment.Center => TextAlignment.Center,
            ParagraphAlignment.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };

        jobs.Add(DrawJob.ForParagraph(block, first, layout, style.Foreground, style.Bold, alignment));
    }

    /// <summary>
    /// Paints each paragraph line into its own box when the translation fits at no less
    /// than 80% of the line's natural font size. Returns false when any line needs more
    /// room, in which case the caller reflows the whole paragraph as one block.
    /// </summary>
    private static bool TryRenderPerLine(
        List<ParagraphLine> entries,
        int width,
        int height,
        List<DrawJob> jobs)
    {
        var layouts = new TextLayout[entries.Count];
        var boxes = new Rect[entries.Count];

        // One cap for the whole paragraph: OCR box heights vary by a few pixels with
        // ascenders/descenders, and per-line caps would paint neighbouring rows at
        // visibly different sizes. The smallest cap wins so every line still fits.
        double sharedCap = FontCap(entries[0].Raw, entries[0].Style.InkHeight);
        for (int i = 1; i < entries.Count; i++)
        {
            sharedCap = Math.Min(sharedCap, FontCap(entries[i].Raw, entries[i].Style.InkHeight));
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var layout = BuildTextLayout(
                entry.Text,
                Math.Max(1, entry.PaintBox.Width - (TextPaddingX * 2)),
                Math.Max(1, entry.PaintBox.Height - (TextPaddingY * 2)),
                sharedCap,
                entry.Style.Bold);

            if (layout.FontSize < Math.Max(MinFontSize + 0.5, sharedCap * 0.8))
            {
                return false;
            }

            layouts[i] = layout;
            boxes[i] = entry.PaintBox;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var layout = layouts[i];
            var grown = GrowBoxToFit(boxes[i], layout.Size.Height + (TextPaddingY * 2), width, height);
            if (grown.Height > boxes[i].Height + 0.5)
            {
                boxes[i] = grown;
                layout = BuildTextLayout(
                    entry.Text,
                    Math.Max(1, grown.Width - (TextPaddingX * 2)),
                    Math.Max(1, grown.Height - (TextPaddingY * 2)),
                    sharedCap,
                    entry.Style.Bold);
            }

            jobs.Add(DrawJob.ForAxis(boxes[i], entry.Raw, layout, entry.Style.Foreground, entry.Style.Bold));
        }

        return true;
    }

    /// <summary>
    /// Vertical room a paragraph may use: down to the next (non-member) line, capped so
    /// one paragraph cannot paint over a large unrelated area.
    /// </summary>
    private static double AvailableHeight(
        IReadOnlyList<OcrTextLine> lines,
        TranslateParagraph paragraph,
        int imageHeight)
    {
        double nextTop = imageHeight;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i] == null || paragraph.LineIndices.Contains(i))
            {
                continue;
            }

            double top = lines[i].Box.Top;
            if (top > paragraph.Box.Top + 1 && top < nextTop)
            {
                nextTop = top;
            }
        }

        double capBottom = Math.Min(
            nextTop - 2,
            paragraph.Box.Top + (paragraph.Box.Height * 2.5) + 60);
        return Math.Max(paragraph.Box.Height, capBottom - paragraph.Box.Top);
    }

    /// <summary>
    /// Joins per-line translations into one paragraph. A space is inserted only when the
    /// boundary is not CJK↔CJK, so Chinese output stays tight and Latin stays readable.
    /// </summary>
    internal static string JoinTranslationParts(IReadOnlyList<string> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var builder = new StringBuilder();
        foreach (string part in parts)
        {
            string trimmed = part?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (builder.Length > 0 && NeedsSpace(builder[^1], trimmed[0]))
            {
                builder.Append(' ');
            }

            builder.Append(trimmed);
        }

        return builder.ToString();
    }

    private static bool NeedsSpace(char left, char right)
        => !(IsCjk(left) && IsCjk(right));

    private static bool IsCjk(char value)
        => (value >= 0x2E80 && value <= 0x9FFF)
        || (value >= 0xF900 && value <= 0xFAFF)
        || (value >= 0xFF00 && value <= 0xFFEF)
        || (value >= 0x3000 && value <= 0x303F);

    /// <summary>
    /// Erases one region and returns the style to paint with. Preferred path: analyze a
    /// glyph mask and erase only those pixels (exact plane fill / flat fill / inpaint).
    /// Fallback (no plausible mask): erase the whole box with a plane or Telea fill.
    /// </summary>
    private static TextStyle EraseSmart(
        Span<byte> pixels,
        int stride,
        int width,
        int height,
        Rect box,
        WriteableBitmap dest)
    {
        var mask = TextMaskAnalyzer.TryBuild(pixels, stride, width, height, box);
        if (mask != null && mask.IsUsable && mask.MaskPixelCount > 0)
        {
            bool filled = mask.FillKind switch
            {
                MaskFillKind.Plane => mask.FillWithPlane(pixels, stride, width, height),
                MaskFillKind.Solid => FillMaskSolid(mask, pixels, stride, width, height),
                _ => LightInpaint.TryInpaintMask(
                    pixels,
                    stride,
                    width,
                    height,
                    mask.Mask,
                    mask.Width,
                    mask.Left,
                    mask.Top,
                    mask.Left + mask.Width,
                    mask.Top + mask.Height,
                    mask.InpaintRadius),
            };

            if (!filled)
            {
                mask.FillSolid(pixels, stride, width, height);
            }

            AddDirtyRegion(dest, mask.Bounds, width, height);
            return new TextStyle(
                mask.Foreground,
                mask.Bold,
                mask.MaskPixelCount,
                Math.Max(0, mask.Bounds.Height - 2));
        }

        // No reliable glyph mask (photo background, very low contrast, tiny box).
        var bg = SampleBackground(pixels, stride, width, height, box);
        var ring = new List<BackgroundSample>(160);
        CollectRingSamples(pixels, stride, width, height, box, ring);
        var plane = BackgroundPlane.Fit(ring);
        if (plane != null)
        {
            plane.FillRect(
                pixels,
                stride,
                width,
                height,
                (int)Math.Floor(box.X),
                (int)Math.Floor(box.Y),
                (int)Math.Ceiling(box.Right),
                (int)Math.Ceiling(box.Bottom));
        }
        else
        {
            EraseRegion(pixels, stride, width, height, box, bg);
        }

        AddDirtyRegion(dest, box, width, height);
        Color foreground = mask is { IsUsable: true } ? mask.Foreground : ContrastingText(bg);
        return new TextStyle(foreground, bold: false, inkPixels: 0, inkHeight: 0);
    }

    private static bool FillMaskSolid(
        TextMaskResult mask,
        Span<byte> pixels,
        int stride,
        int width,
        int height)
    {
        mask.FillSolid(pixels, stride, width, height);
        return true;
    }

    /// <summary>
    /// Measures and wraps the translation once, up front: every draw call must use
    /// the same numbers, otherwise a size that merely "fits" by measurement can be
    /// clipped away entirely at draw time.
    /// </summary>
    private static TextLayout BuildTextLayout(
        string text,
        double maxWidth,
        double maxHeight,
        double maxFontSize,
        bool bold)
    {
        var cache = new TextMeasureCache(bold);
        double fontSize = FitFontSize(text, maxWidth, maxHeight, maxFontSize, cache);
        string display = WrapTextCore(text, maxWidth, fontSize, cache);
        var size = MeasureUnclipped(display, fontSize, maxWidth, bold);
        return new TextLayout(display, fontSize, size);
    }

    /// <summary>
    /// Grows <paramref name="paintBox"/> vertically (down first, then up, clamped to
    /// the image) when the laid-out translation is taller than the OCR box, so long
    /// translations keep their tail instead of being cut after the first line.
    /// Growth is capped so a short OCR line cannot erase an unbounded page area.
    /// </summary>
    internal static Rect GrowBoxToFit(Rect paintBox, double neededHeight, int width, int height)
    {
        if (height <= 0 || neededHeight <= paintBox.Height + 0.5)
        {
            return paintBox;
        }

        double cap = Math.Min(height, Math.Max(paintBox.Height * 2, paintBox.Height + MaxBoxGrowth));
        double target = Math.Min(neededHeight, cap);
        double y = paintBox.Y;
        if (y + target > height)
        {
            y = Math.Max(0, height - target);
        }

        return Clamp(new Rect(paintBox.X, y, paintBox.Width, target), width, height);
    }

    /// <summary>
    /// Grows a paint box vertically while the rows just outside it still hold ink.
    /// OCR boxes hug the x-height, so ascenders/descenders of the very line being
    /// translated poke out of the fixed expansion. The mask then samples those border
    /// strokes and drags their color inward (grey smudges), while the uncovered tips
    /// survive as stray "black dots". Growth is capped at a fraction of the box height
    /// so a tight line pitch cannot swallow the neighbouring line.
    /// </summary>
    internal static Rect ExpandToCoverInk(ReadOnlySpan<byte> pixels, int stride, int width, int height, Rect box, Color background)
    {
        if (box.Height <= 0 || box.Width <= 0 || width <= 0 || height <= 0)
        {
            return box;
        }

        int x0 = Math.Clamp((int)Math.Floor(box.X), 0, width - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(box.Right), x0 + 1, width);
        int bgLuma = (background.R + background.G + background.B) / 3;
        int maxExtra = Math.Max(2, Math.Min(6, (int)Math.Ceiling(box.Height * 0.4)));

        int top = (int)Math.Floor(box.Y);
        for (int extra = 1; extra <= maxExtra; extra++)
        {
            int row = top - 1;
            if (row < 0 || !RowHasInk(pixels, stride, x0, x1, row, bgLuma))
            {
                break;
            }

            top = row;
        }

        int bottom = (int)Math.Ceiling(box.Bottom);
        for (int extra = 1; extra <= maxExtra; extra++)
        {
            int row = bottom;
            if (row >= height || !RowHasInk(pixels, stride, x0, x1, row, bgLuma))
            {
                break;
            }

            bottom = row + 1;
        }

        // Inline-code backgrounds extend past the glyph box as flat light-grey strips;
        // covering a couple more rows keeps those from surviving as faint smears.
        for (int extra = 1; extra <= TintRowBudget; extra++)
        {
            int row = top - 1;
            if (row < 0 || !RowHasTint(pixels, stride, x0, x1, row, bgLuma))
            {
                break;
            }

            top = row;
        }

        for (int extra = 1; extra <= TintRowBudget; extra++)
        {
            int row = bottom;
            if (row >= height || !RowHasTint(pixels, stride, x0, x1, row, bgLuma))
            {
                break;
            }

            bottom = row + 1;
        }

        return Clamp(new Rect(box.X, top, box.Width, bottom - top), width, height);
    }

    private static bool RowHasInk(ReadOnlySpan<byte> pixels, int stride, int x0, int x1, int y, int backgroundLuma)
    {
        int offset = y * stride;
        for (int x = x0; x < x1; x++)
        {
            int i = offset + (x * 4);
            int luma = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
            if (Math.Abs(luma - backgroundLuma) > InkDelta)
            {
                return true;
            }
        }

        return false;
    }

    private static bool RowHasTint(ReadOnlySpan<byte> pixels, int stride, int x0, int x1, int y, int backgroundLuma)
    {
        int offset = y * stride;
        int tinted = 0;
        for (int x = x0; x < x1; x++)
        {
            int i = offset + (x * 4);
            int luma = (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3;
            if (Math.Abs(luma - backgroundLuma) > TintDelta)
            {
                tinted++;
            }
        }

        return tinted * 100 >= (x1 - x0) * (int)(MinTintFraction * 100);
    }

    private static void BlitAxisText(WriteableBitmap dest, DrawJob job)
    {
        var region = ComputeBlitRegion(job.Box, dest.PixelWidth, dest.PixelHeight);
        if (region.IsEmpty)
        {
            return;
        }

        // Draw in region-local space so a box clipped at the image edge keeps its layout.
        var localBox = new Rect(job.Box.X - region.X, job.Box.Y - region.Y, job.Box.Width, job.Box.Height);
        var localAnchor = new Rect(job.Anchor.X - region.X, job.Anchor.Y - region.Y, job.Anchor.Width, job.Anchor.Height);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            DrawAxisText(dc, localBox, localAnchor, job.Layout, job.Foreground, job.Bold);
        }

        var rtb = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        AlphaBlendInto(dest, rtb, region.X, region.Y);
    }

    private static void BlitParagraphText(WriteableBitmap dest, DrawJob job)
    {
        var region = ComputeBlitRegion(job.Box, dest.PixelWidth, dest.PixelHeight);
        if (region.IsEmpty)
        {
            return;
        }

        var localBox = new Rect(job.Box.X - region.X, job.Box.Y - region.Y, job.Box.Width, job.Box.Height);
        var localAnchor = new Rect(job.Anchor.X - region.X, job.Anchor.Y - region.Y, job.Anchor.Width, job.Anchor.Height);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            DrawParagraphText(dc, localBox, localAnchor, job.Layout, job.Foreground, job.Bold, job.Alignment);
        }

        var rtb = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        AlphaBlendInto(dest, rtb, region.X, region.Y);
    }

    private static void BlitQuadText(WriteableBitmap dest, DrawJob job)
    {
        var region = ComputeBlitRegion(BoundsOf(job.Quad), dest.PixelWidth, dest.PixelHeight);
        if (region.IsEmpty)
        {
            return;
        }

        // Draw in region-local space: translate quad so region top-left is (0,0).
        var localQuad = OffsetQuad(job.Quad, -region.X, -region.Y);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            DrawQuadText(dc, localQuad, job.LocalWidth, job.LocalHeight, job.Layout, job.Foreground, job.Bold);
        }

        var rtb = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        AlphaBlendInto(dest, rtb, region.X, region.Y);
    }

    /// <summary>
    /// Integer pixel region covering <paramref name="bounds"/>, clipped to the image
    /// (floor left/top, ceil right/bottom). Empty when nothing is inside the image.
    /// </summary>
    internal static Int32Rect ComputeBlitRegion(Rect bounds, int imageWidth, int imageHeight)
    {
        if (bounds.IsEmpty || imageWidth <= 0 || imageHeight <= 0
            || double.IsNaN(bounds.X) || double.IsNaN(bounds.Y)
            || double.IsNaN(bounds.Width) || double.IsNaN(bounds.Height))
        {
            return Int32Rect.Empty;
        }

        int left = (int)Math.Floor(Math.Max(0, bounds.X));
        int top = (int)Math.Floor(Math.Max(0, bounds.Y));
        int right = (int)Math.Ceiling(Math.Min(imageWidth, bounds.Right));
        int bottom = (int)Math.Ceiling(Math.Min(imageHeight, bounds.Bottom));
        if (right <= left || bottom <= top)
        {
            return Int32Rect.Empty;
        }

        return new Int32Rect(left, top, right - left, bottom - top);
    }

    internal static Point[] OffsetQuad(IReadOnlyList<Point> quad, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(quad);
        var result = new Point[quad.Count];
        for (int i = 0; i < quad.Count; i++)
        {
            result[i] = new Point(quad[i].X + dx, quad[i].Y + dy);
        }

        return result;
    }

    /// <summary>Pbgra32 overlay → Bgra32 dest via <see cref="BlendPremultipliedOver"/>.</summary>
    private static void AlphaBlendInto(WriteableBitmap dest, RenderTargetBitmap src, int destX, int destY)
    {
        int sw = src.PixelWidth;
        int sh = src.PixelHeight;
        int srcStride = sw * 4;
        int byteCount = checked(srcStride * sh);
        byte[] srcBuf = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            src.CopyPixels(srcBuf, srcStride, 0);

            dest.Lock();
            try
            {
                int dstStride = dest.BackBufferStride;
                int dw = dest.PixelWidth;
                int dh = dest.PixelHeight;
                Span<byte> dst;
                unsafe
                {
                    dst = new Span<byte>((void*)dest.BackBuffer, checked(dstStride * dh));
                }

                var dirty = BlendPremultipliedOver(
                    srcBuf.AsSpan(0, byteCount), srcStride, sw, sh, dst, dstStride, dw, dh, destX, destY);
                if (!dirty.IsEmpty)
                {
                    dest.AddDirtyRect(dirty);
                }
            }
            finally
            {
                dest.Unlock();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(srcBuf);
        }
    }

    /// <summary>
    /// Source-over blend of a premultiplied BGRA overlay onto an opaque BGRA buffer:
    /// <c>dst = src + dst * (255 - a) / 255</c>, alpha forced to 255. Pixels falling
    /// outside the destination are clipped. Returns the touched destination rect.
    /// </summary>
    internal static Int32Rect BlendPremultipliedOver(
        ReadOnlySpan<byte> src,
        int srcStride,
        int srcWidth,
        int srcHeight,
        Span<byte> dst,
        int dstStride,
        int dstWidth,
        int dstHeight,
        int destX,
        int destY)
    {
        int x0 = Math.Max(0, destX);
        int y0 = Math.Max(0, destY);
        int x1 = Math.Min(dstWidth, destX + srcWidth);
        int y1 = Math.Min(dstHeight, destY + srcHeight);
        if (x1 <= x0 || y1 <= y0)
        {
            return Int32Rect.Empty;
        }

        for (int dy = y0; dy < y1; dy++)
        {
            var srcRow = src.Slice(((dy - destY) * srcStride) + ((x0 - destX) * 4), (x1 - x0) * 4);
            var dstRow = dst.Slice((dy * dstStride) + (x0 * 4), (x1 - x0) * 4);
            for (int o = 0; o < srcRow.Length; o += 4)
            {
                byte sa = srcRow[o + 3];
                if (sa == 0)
                {
                    continue;
                }

                if (sa == 255)
                {
                    dstRow[o] = srcRow[o];
                    dstRow[o + 1] = srcRow[o + 1];
                    dstRow[o + 2] = srcRow[o + 2];
                    dstRow[o + 3] = 255;
                    continue;
                }

                int inv = 255 - sa;
                dstRow[o] = (byte)(((srcRow[o] * 255) + (dstRow[o] * inv)) / 255);
                dstRow[o + 1] = (byte)(((srcRow[o + 1] * 255) + (dstRow[o + 1] * inv)) / 255);
                dstRow[o + 2] = (byte)(((srcRow[o + 2] * 255) + (dstRow[o + 2] * inv)) / 255);
                dstRow[o + 3] = 255;
            }
        }

        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
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

    /// <summary>Marks only the erased box dirty (not the full bitmap).</summary>
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

    private static void AddDirtyRegion(WriteableBitmap bitmap, Int32Rect region, int width, int height)
        => AddDirtyRegion(bitmap, new Rect(region.X, region.Y, region.Width, region.Height), width, height);

    private static void DrawAxisText(
        DrawingContext dc,
        Rect paintBox,
        Rect anchor,
        TextLayout layout,
        Color fg,
        bool bold)
    {
        double contentWidth = Math.Max(1, paintBox.Width - (TextPaddingX * 2));
        var ft = CreateLaidOutText(layout.Display, layout.FontSize, contentWidth, fg, bold);
        double y = BaselineTop(paintBox, anchor, layout, ft);
        dc.DrawText(ft, new Point(paintBox.X + TextPaddingX, y));
    }

    private static void DrawParagraphText(
        DrawingContext dc,
        Rect block,
        Rect anchor,
        TextLayout layout,
        Color fg,
        bool bold,
        TextAlignment alignment)
    {
        double contentWidth = Math.Max(1, block.Width - (TextPaddingX * 2));
        var ft = CreateLaidOutText(layout.Display, layout.FontSize, contentWidth, fg, bold);
        ft.TextAlignment = alignment;
        double y = BaselineTop(block, anchor, layout, ft);
        dc.DrawText(ft, new Point(block.X + TextPaddingX, y));
    }

    /// <summary>
    /// Places the first text baseline on the original line's baseline (box bottom minus
    /// an estimated descent), clamped so the block stays inside the paint box. When the
    /// block is taller than the box (grown for a long translation) it starts at the top.
    /// </summary>
    private static double BaselineTop(Rect paintBox, Rect anchor, TextLayout layout, FormattedText ft)
    {
        double descent = Math.Min(Math.Max(0, anchor.Height) * 0.18, layout.FontSize * 0.22);
        double y = anchor.Bottom - descent - ft.Baseline;
        double minY = paintBox.Y + TextPaddingY;
        if (y < minY)
        {
            y = minY;
        }

        double maxY = paintBox.Bottom - TextPaddingY - ft.Height;
        if (maxY >= minY && y > maxY)
        {
            y = maxY;
        }

        return y;
    }

    private static void DrawQuadText(
        DrawingContext dc,
        Point[] quad,
        double localW,
        double localH,
        TextLayout layout,
        Color fg,
        bool bold)
    {
        var matrix = CreateAffineFromQuad(quad, localW, localH);
        dc.PushTransform(new MatrixTransform(matrix));
        try
        {
            double contentWidth = Math.Max(1, localW - (TextPaddingX * 2));
            var ft = CreateLaidOutText(layout.Display, layout.FontSize, contentWidth, fg, bold);

            double descent = Math.Min(localH * 0.18, layout.FontSize * 0.22);
            double y = localH - TextPaddingY - descent - ft.Baseline;
            double minY = TextPaddingY;
            if (y < minY)
            {
                y = minY;
            }

            dc.DrawText(ft, new Point(TextPaddingX, y));
        }
        finally
        {
            dc.Pop();
        }
    }

    private readonly struct TextStyle
    {
        public TextStyle(Color foreground, bool bold, int inkPixels, double inkHeight)
        {
            Foreground = foreground;
            Bold = bold;
            InkPixels = inkPixels;
            InkHeight = inkHeight;
        }

        public Color Foreground { get; }

        public bool Bold { get; }

        /// <summary>Mask pixels erased for this line; the paragraph keeps the busiest style.</summary>
        public int InkPixels { get; }

        /// <summary>Height of the detected glyph ink (0 when no mask was usable).</summary>
        public double InkHeight { get; }
    }

    private readonly struct ParagraphLine
    {
        public ParagraphLine(Rect raw, Rect paintBox, TextStyle style, string text)
        {
            Raw = raw;
            PaintBox = paintBox;
            Style = style;
            Text = text;
        }

        /// <summary>Original OCR box (baseline anchor).</summary>
        public Rect Raw { get; }

        /// <summary>Expanded erase/paint box.</summary>
        public Rect PaintBox { get; }

        public TextStyle Style { get; }

        public string Text { get; }
    }

    private readonly struct TextLayout
    {
        public TextLayout(string display, double fontSize, Size size)
        {
            Display = display;
            FontSize = fontSize;
            Size = size;
        }

        /// <summary>Already wrapped text (no further layout decisions at draw time).</summary>
        public string Display { get; }

        public double FontSize { get; }

        /// <summary>Unclipped laid-out size of <see cref="Display"/> at <see cref="FontSize"/>.</summary>
        public Size Size { get; }
    }

    private enum DrawKind
    {
        Axis,
        Quad,
        Paragraph,
    }

    private sealed class DrawJob
    {
        private DrawJob()
        {
        }

        public DrawKind Kind { get; private init; }

        /// <summary>Paint/paragraph box in image coordinates.</summary>
        public Rect Box { get; private init; }

        /// <summary>Original first-line box used as the baseline anchor.</summary>
        public Rect Anchor { get; private init; }

        public Point[] Quad { get; private init; }

        public double LocalWidth { get; private init; }

        public double LocalHeight { get; private init; }

        public TextLayout Layout { get; private init; }

        public Color Foreground { get; private init; }

        public bool Bold { get; private init; }

        public TextAlignment Alignment { get; private init; }

        public static DrawJob ForAxis(Rect paintBox, Rect anchor, TextLayout layout, Color fg, bool bold)
            => new DrawJob
            {
                Kind = DrawKind.Axis,
                Box = paintBox,
                Anchor = anchor,
                Layout = layout,
                Foreground = fg,
                Bold = bold,
            };

        public static DrawJob ForParagraph(
            Rect block,
            Rect anchor,
            TextLayout layout,
            Color fg,
            bool bold,
            TextAlignment alignment)
            => new DrawJob
            {
                Kind = DrawKind.Paragraph,
                Box = block,
                Anchor = anchor,
                Layout = layout,
                Foreground = fg,
                Bold = bold,
                Alignment = alignment,
            };

        public static DrawJob ForQuad(
            Point[] quad,
            double localW,
            double localH,
            TextLayout layout,
            Color fg,
            bool bold)
            => new DrawJob
            {
                Kind = DrawKind.Quad,
                Quad = quad,
                LocalWidth = localW,
                LocalHeight = localH,
                Layout = layout,
                Foreground = fg,
                Bold = bold,
            };
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
    /// Collects background samples on rings around <paramref name="box"/> (positions
    /// plus colors, for both the median and the plane fit).
    /// </summary>
    internal static void CollectRingSamples(
        ReadOnlySpan<byte> pixels,
        int stride,
        int width,
        int height,
        Rect box,
        List<BackgroundSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
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
                AddBackgroundSample(pixels, samples, stride, width, height, x, top - ring);
                AddBackgroundSample(pixels, samples, stride, width, height, x, bottom + ring - 1);
            }

            for (int y = top; y <= bottom; y += stepY)
            {
                AddBackgroundSample(pixels, samples, stride, width, height, left - ring, y);
                AddBackgroundSample(pixels, samples, stride, width, height, right + ring - 1, y);
            }

            AddBackgroundSample(pixels, samples, stride, width, height, left - ring, top - ring);
            AddBackgroundSample(pixels, samples, stride, width, height, right + ring - 1, top - ring);
            AddBackgroundSample(pixels, samples, stride, width, height, left - ring, bottom + ring - 1);
            AddBackgroundSample(pixels, samples, stride, width, height, right + ring - 1, bottom + ring - 1);
        }
    }

    /// <summary>
    /// Multi-ring samples around <paramref name="box"/> and returns the per-channel median.
    /// Avoids the muddy average of mixed edge pixels on complex backgrounds.
    /// </summary>
    internal static Color SampleBackground(ReadOnlySpan<byte> pixels, int stride, int width, int height, Rect box)
    {
        var ring = new List<BackgroundSample>(64);
        CollectRingSamples(pixels, stride, width, height, box, ring);

        var samples = new List<(byte R, byte G, byte B)>(ring.Count + 5);
        foreach (var sample in ring)
        {
            samples.Add((sample.R, sample.G, sample.B));
        }

        if (samples.Count == 0)
        {
            int left = (int)Math.Floor(box.X);
            int top = (int)Math.Floor(box.Y);
            int right = (int)Math.Ceiling(box.Right);
            int bottom = (int)Math.Ceiling(box.Bottom);
            TryAppendSample(pixels, samples, stride, width, height, left + 1, top + 1);
            TryAppendSample(pixels, samples, stride, width, height, right - 2, top + 1);
            TryAppendSample(pixels, samples, stride, width, height, left + 1, bottom - 2);
            TryAppendSample(pixels, samples, stride, width, height, right - 2, bottom - 2);
            TryAppendSample(pixels, samples, stride, width, height, (left + right) / 2, (top + bottom) / 2);
        }

        if (samples.Count == 0)
        {
            return Colors.White;
        }

        return MedianColor(samples);
    }

    private static void AddBackgroundSample(
        ReadOnlySpan<byte> pixels,
        List<BackgroundSample> samples,
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
        samples.Add(new BackgroundSample(px, py, pixels[i + 2], pixels[i + 1], pixels[i]));
    }

    private static void TryAppendSample(
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
        int n = samples.Count;
        if (n == 0)
        {
            return Colors.White;
        }

        // Single-pass fill + Span.Sort — avoids 3× Select().ToArray() on the hot sample path.
        // Cap stackalloc so huge caller-built lists fall back to heap without blowing the stack.
        const int stackCap = 512;
        Span<byte> rs = n <= stackCap ? stackalloc byte[n] : new byte[n];
        Span<byte> gs = n <= stackCap ? stackalloc byte[n] : new byte[n];
        Span<byte> bs = n <= stackCap ? stackalloc byte[n] : new byte[n];
        for (int i = 0; i < n; i++)
        {
            var s = samples[i];
            rs[i] = s.R;
            gs[i] = s.G;
            bs[i] = s.B;
        }

        rs.Sort();
        gs.Sort();
        bs.Sort();
        int mid = n / 2;
        if ((n % 2) == 0)
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

        return WrapTextCore(text, maxWidth, fontSize, new TextMeasureCache(bold: false));
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
        => FitFontSize(text, maxWidth, maxHeight, MaxFontSize, new TextMeasureCache(bold: false));

    private static double FitFontSize(
        string text,
        double maxWidth,
        double maxHeight,
        double maxFontSize,
        TextMeasureCache cache)
    {
        double lo = MinFontSize;
        double hi = Math.Max(
            MinFontSize + 0.5,
            Math.Min(Math.Min(MaxFontSize, maxFontSize), maxHeight));
        double best = MinFontSize;

        // One cache shared across binary-search iterations + wrap measurements.
        for (int i = 0; i < 12; i++)
        {
            double mid = (lo + hi) / 2;
            var display = WrapTextCore(text, maxWidth, mid, cache);
            var size = MeasureUnclipped(display, mid, maxWidth, cache.Bold);
            if (size.Width <= maxWidth + 0.5 && size.Height <= maxHeight + 0.5)
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
        string display,
        double fontSize,
        double maxWidth,
        Color foreground,
        bool bold)
    {
        Brush brush = foreground == Colors.Black ? Brushes.Black : new SolidColorBrush(foreground);

        // No MaxTextHeight: WPF drops the whole line (Width/Height become 0) when the
        // line box is a fraction taller than the clip height, which erased the source
        // text and drew nothing. Overflow is clipped by the blit region instead.
        return new FormattedText(
            display,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            bold ? BoldTypeface : SemiBoldTypeface,
            fontSize,
            brush,
            1.0)
        {
            MaxTextWidth = maxWidth,
            Trimming = TextTrimming.None,
        };
    }

    /// <summary>
    /// True laid-out size of the wrapped text with only MaxTextWidth applied.
    /// Setting MaxTextHeight would clamp FormattedText.Height to the box, hiding
    /// overflow from FitFontSize's binary search (it then always "fits" and never
    /// shrinks).
    /// </summary>
    private static Size MeasureUnclipped(string display, double fontSize, double maxWidth, bool bold)
    {
        var ft = new FormattedText(
            display,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            bold ? BoldTypeface : SemiBoldTypeface,
            fontSize,
            Brushes.Black,
            1.0)
        {
            MaxTextWidth = maxWidth,
            Trimming = TextTrimming.None,
        };

        return new Size(ft.Width, ft.Height);
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

        public TextMeasureCache(bool bold)
        {
            Bold = bold;
        }

        public bool Bold { get; }

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
                Bold ? BoldTypeface : SemiBoldTypeface,
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
                _ = (Bold ? BoldTypeface : SemiBoldTypeface).TryGetGlyphTypeface(out _glyphTypeface);
            }

            glyph = _glyphTypeface;
            return glyph != null;
        }

        private static int SizeKey(double fontSize)
            => (int)Math.Round(fontSize * 100.0);
    }
}
