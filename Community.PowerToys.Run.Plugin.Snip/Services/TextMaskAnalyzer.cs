// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Builds the glyph mask and original text style for one OCR box.
///
/// Background candidates are tried in order of quality: a least-squares plane fitted
/// to the ring around the box (flat surfaces and linear gradients), the interior
/// median (a pill/code-block color), then the ring median. The first candidate whose
/// ink fraction is plausible wins, which also avoids the inverted-mask failure when a
/// dense box makes the interior median land on the text color.
/// </summary>
internal static class TextMaskAnalyzer
{
    /// <summary>Smallest box side the analyzer will handle (pixels).</summary>
    internal const int MinBoxSide = 6;

    /// <summary>Above this ink fraction the "mask" is background/texture, not glyphs.</summary>
    internal const double MaxInkFraction = 0.55;

    /// <summary>Below this ink fraction there is nothing worth erasing.</summary>
    internal const double MinInkFraction = 0.004;

    /// <summary>Fewer core pixels than this cannot describe a text line.</summary>
    internal const int MinCorePixels = 12;

    /// <summary>Interior/ring background delta that marks a plate (pill, code block).</summary>
    internal const int PlateDelta = 12;

    /// <summary>Ring roughness (max-channel MAD) still treated as a flat surface.</summary>
    internal const int FlatRoughness = 4;

    private const int MinThreshold = 20;
    private const int MaxThreshold = 140;
    private const double MinContrastRatio = 2.5;
    private const int MaxInpaintRadius = 16;

    /// <summary>
    /// Analyzes <paramref name="box"/> in a BGRA32 buffer. Returns null when the box
    /// is too small to analyze; otherwise <see cref="TextMaskResult.IsUsable"/> says
    /// whether the mask may replace whole-box erase.
    /// </summary>
    public static TextMaskResult TryBuild(
        ReadOnlySpan<byte> pixels,
        int stride,
        int width,
        int height,
        Rect box)
    {
        int x0 = Math.Clamp((int)Math.Floor(box.X), 0, width);
        int y0 = Math.Clamp((int)Math.Floor(box.Y), 0, height);
        int x1 = Math.Clamp((int)Math.Ceiling(box.Right), 0, width);
        int y1 = Math.Clamp((int)Math.Ceiling(box.Bottom), 0, height);
        int rw = x1 - x0;
        int rh = y1 - y0;
        if (rw < MinBoxSide || rh < MinBoxSide)
        {
            return null;
        }

        int total = rw * rh;

        // Ring samples: both the flat median and the plane fit use them.
        var ring = new List<BackgroundSample>(160);
        ImageTranslateRenderer.CollectRingSamples(pixels, stride, width, height, box, ring);
        if (ring.Count < BackgroundPlane.MinSamples)
        {
            return null;
        }

        Color ringBg = MedianOf(ring);
        Color innerBg = MedianInside(pixels, stride, x0, y0, x1, y1);
        int innerRoughness = InteriorRoughness(pixels, stride, x0, y0, x1, y1, innerBg);
        BackgroundPlane plane = BackgroundPlane.Fit(ring);
        int roughness = RingRoughness(ring, ringBg);

        // A plate only counts when the interior is actually a flat surface; on a noisy
        // photo the interior median differs from the ring by chance and must not turn
        // the fill into a flat patch.
        bool hasPlate = MaxChannelDelta(innerBg, ringBg) >= PlateDelta
            && innerRoughness <= FlatRoughness;

        // Candidates are scored rather than trusted blindly:
        //  · a detected plate (pill/code block) must use the interior color, because the
        //    ring plane describes the page *outside* the plate;
        //  · otherwise the plane is best (flat surfaces and gradients);
        //  · a dense box can push the interior median onto the ink itself, in which case
        //    its candidate is implausible and the plane/ring wins.
        var candidates = new List<EvaluatedCandidate>(3);
        if (hasPlate)
        {
            candidates.Add(Evaluate(pixels, stride, x0, y0, rw, rh, total, new MaskCandidate(null, innerBg, usePlane: false)));
            if (plane != null)
            {
                candidates.Add(Evaluate(pixels, stride, x0, y0, rw, rh, total, new MaskCandidate(plane, default, usePlane: true)));
            }
        }
        else
        {
            if (plane != null)
            {
                candidates.Add(Evaluate(pixels, stride, x0, y0, rw, rh, total, new MaskCandidate(plane, default, usePlane: true)));
            }

            candidates.Add(Evaluate(pixels, stride, x0, y0, rw, rh, total, new MaskCandidate(null, innerBg, usePlane: false)));
        }

        if (MaxChannelDelta(innerBg, ringBg) >= 4)
        {
            candidates.Add(Evaluate(pixels, stride, x0, y0, rw, rh, total, new MaskCandidate(null, ringBg, usePlane: false)));
        }

        EvaluatedCandidate chosen = candidates[0];
        foreach (var candidate in candidates)
        {
            if (!candidate.Plausible)
            {
                continue;
            }

            chosen = candidate;
            break;
        }

        byte[] chosenDistances = chosen.Distances;
        int chosenThreshold = chosen.Threshold;
        bool chosenUsePlane = chosen.Candidate.UsePlane;
        Color chosenBackground = chosenUsePlane
            ? chosen.Candidate.Plane.At((x0 + x1) / 2, (y0 + y1) / 2)
            : chosen.Candidate.Background;

        // Core mask (before dilation): used for foreground color, stroke width, ink stats.
        var core = new byte[total];
        int coreCount = 0;
        for (int i = 0; i < total; i++)
        {
            if (chosenDistances[i] >= chosenThreshold)
            {
                core[i] = 1;
                coreCount++;
            }
        }

        bool isUsable = coreCount >= MinCorePixels
            && coreCount <= total * MaxInkFraction;

        (Color foreground, double strokeWidth) = ExtractForeground(
            pixels, stride, core, chosenDistances, chosenThreshold, coreCount, x0, y0, rw, rh, chosenBackground);

        // Dilation covers the anti-aliased fringe that fell below the threshold.
        byte[] mask = Dilate(core, rw, rh, out Int32Rect bounds, out int maskPixels);

        MaskFillKind fillKind;
        if (chosenUsePlane)
        {
            fillKind = MaskFillKind.Plane;
        }
        else if (hasPlate || roughness <= FlatRoughness)
        {
            fillKind = MaskFillKind.Solid;
        }
        else
        {
            fillKind = MaskFillKind.Inpaint;
        }

        bool bold = coreCount >= 20
            && strokeWidth >= Math.Max(2.8, 0.115 * rh);

        return new TextMaskResult
        {
            Mask = mask,
            Width = rw,
            Height = rh,
            Left = x0,
            Top = y0,
            Background = chosenBackground,
            Foreground = foreground,
            Bold = bold,
            InkFraction = (double)coreCount / total,
            HasPlate = hasPlate,
            Plane = chosenUsePlane ? chosen.Candidate.Plane : null,
            FillKind = fillKind,
            InpaintRadius = Math.Clamp((int)Math.Ceiling(strokeWidth) + 2, 4, MaxInpaintRadius),
            MaskPixelCount = maskPixels,
            Bounds = new Int32Rect(
                x0 + bounds.X,
                y0 + bounds.Y,
                bounds.Width,
                bounds.Height),
            IsUsable = isUsable,
        };
    }

    private static EvaluatedCandidate Evaluate(
        ReadOnlySpan<byte> pixels,
        int stride,
        int x0,
        int y0,
        int rw,
        int rh,
        int total,
        MaskCandidate candidate)
    {
        byte[] distances = BuildDistances(
            pixels, stride, x0, y0, rw, rh, candidate, out int[] histogram);
        int threshold = Math.Clamp(Otsu(histogram, total), MinThreshold, MaxThreshold);
        int core = CountAtLeast(distances, threshold);
        double fraction = total == 0 ? 0 : (double)core / total;
        bool plausible = core >= MinCorePixels
            && fraction >= MinInkFraction
            && fraction <= MaxInkFraction;
        return new EvaluatedCandidate(candidate, distances, threshold, plausible);
    }

    private static byte[] BuildDistances(
        ReadOnlySpan<byte> pixels,
        int stride,
        int x0,
        int y0,
        int rw,
        int rh,
        MaskCandidate candidate,
        out int[] histogram)
    {
        histogram = new int[256];
        var distances = new byte[rw * rh];
        for (int y = 0; y < rh; y++)
        {
            int row = (y0 + y) * stride;
            int local = y * rw;
            for (int x = 0; x < rw; x++)
            {
                int i = row + ((x0 + x) * 4);
                int distance = candidate.UsePlane
                    ? candidate.Plane.Distance(pixels[i], pixels[i + 1], pixels[i + 2], x0 + x, y0 + y)
                    : MaxChannelDelta(pixels[i], pixels[i + 1], pixels[i + 2], candidate.Background);
                byte value = (byte)Math.Min(255, distance);
                distances[local + x] = value;
                histogram[value]++;
            }
        }

        return distances;
    }

    /// <summary>
    /// Median color of the most ink-like core pixels (top quarter by distance), which
    /// skips anti-aliased edges that would otherwise pull the color toward the background.
    /// </summary>
    private static (Color Foreground, double StrokeWidth) ExtractForeground(
        ReadOnlySpan<byte> pixels,
        int stride,
        byte[] core,
        byte[] distances,
        int threshold,
        int coreCount,
        int x0,
        int y0,
        int rw,
        int rh,
        Color background)
    {
        if (coreCount < 4)
        {
            return (ImageTranslateRenderer.ContrastingText(background), 0);
        }

        var inkPixels = new List<(int Distance, byte B, byte G, byte R)>(coreCount);
        int runs = 0;
        for (int y = 0; y < rh; y++)
        {
            bool previous = false;
            int row = (y0 + y) * stride;
            int local = y * rw;
            for (int x = 0; x < rw; x++)
            {
                bool isInk = core[local + x] != 0;
                if (isInk)
                {
                    int i = row + ((x0 + x) * 4);
                    inkPixels.Add((distances[local + x], pixels[i], pixels[i + 1], pixels[i + 2]));
                    if (!previous)
                    {
                        runs++;
                    }
                }

                previous = isInk;
            }
        }

        inkPixels.Sort((a, b) => b.Distance.CompareTo(a.Distance));
        int take = Math.Max(4, inkPixels.Count / 4);
        var rs = new byte[take];
        var gs = new byte[take];
        var bs = new byte[take];
        for (int i = 0; i < take; i++)
        {
            bs[i] = inkPixels[i].B;
            gs[i] = inkPixels[i].G;
            rs[i] = inkPixels[i].R;
        }

        Array.Sort(rs);
        Array.Sort(gs);
        Array.Sort(bs);
        int mid = take / 2;
        Color ink = Color.FromRgb(rs[mid], gs[mid], bs[mid]);

        double strokeWidth = runs > 0 ? (double)coreCount / runs : 0;

        // Keep the original color only when it reads clearly against the background;
        // otherwise black/white is safer than a low-contrast sampled hue.
        double ratio = ContrastRatio(ink, background);
        if (ratio < MinContrastRatio)
        {
            return (ImageTranslateRenderer.ContrastingText(background), strokeWidth);
        }

        return (ink, strokeWidth);
    }

    private static byte[] Dilate(byte[] core, int rw, int rh, out Int32Rect bounds, out int maskPixels)
    {
        var mask = new byte[rw * rh];
        int minX = rw, minY = rh, maxX = -1, maxY = -1;
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
            {
                if (core[(y * rw) + x] == 0)
                {
                    continue;
                }

                int yStart = Math.Max(0, y - 1);
                int yEnd = Math.Min(rh - 1, y + 1);
                int xStart = Math.Max(0, x - 1);
                int xEnd = Math.Min(rw - 1, x + 1);
                for (int ny = yStart; ny <= yEnd; ny++)
                {
                    for (int nx = xStart; nx <= xEnd; nx++)
                    {
                        mask[(ny * rw) + nx] = 1;
                    }
                }

                if (x < minX)
                {
                    minX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y > maxY)
                {
                    maxY = y;
                }
            }
        }

        if (maxX < 0)
        {
            bounds = Int32Rect.Empty;
            maskPixels = 0;
            return mask;
        }

        int left = Math.Max(0, minX - 1);
        int top = Math.Max(0, minY - 1);
        int right = Math.Min(rw, maxX + 2);
        int bottom = Math.Min(rh, maxY + 2);
        bounds = new Int32Rect(left, top, right - left, bottom - top);

        maskPixels = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            maskPixels += mask[i];
        }

        return mask;
    }

    private static int CountAtLeast(ReadOnlySpan<byte> distances, int threshold)
    {
        int count = 0;
        for (int i = 0; i < distances.Length; i++)
        {
            if (distances[i] >= threshold)
            {
                count++;
            }
        }

        return count;
    }

    private static int Otsu(ReadOnlySpan<int> histogram, int total)
    {
        double sum = 0;
        for (int i = 0; i < 256; i++)
        {
            sum += i * histogram[i];
        }

        double sumB = 0;
        double weightB = 0;
        double best = 0;
        int threshold = 0;
        for (int t = 0; t < 256; t++)
        {
            weightB += histogram[t];
            if (weightB == 0)
            {
                continue;
            }

            double weightF = total - weightB;
            if (weightF <= 0)
            {
                break;
            }

            sumB += t * histogram[t];
            double meanB = sumB / weightB;
            double meanF = (sum - sumB) / weightF;
            double between = weightB * weightF * (meanB - meanF) * (meanB - meanF);
            if (between > best)
            {
                best = between;
                threshold = t;
            }
        }

        return threshold;
    }

    private static Color MedianOf(IReadOnlyList<BackgroundSample> samples)
    {
        int n = samples.Count;
        var rs = new byte[n];
        var gs = new byte[n];
        var bs = new byte[n];
        for (int i = 0; i < n; i++)
        {
            rs[i] = samples[i].R;
            gs[i] = samples[i].G;
            bs[i] = samples[i].B;
        }

        Array.Sort(rs);
        Array.Sort(gs);
        Array.Sort(bs);
        int mid = n / 2;
        return Color.FromRgb(rs[mid], gs[mid], bs[mid]);
    }

    /// <summary>Median color of the box interior, sampled on a 2-pixel grid.</summary>
    private static Color MedianInside(
        ReadOnlySpan<byte> pixels,
        int stride,
        int x0,
        int y0,
        int x1,
        int y1)
    {
        int[] histogramB = new int[256];
        int[] histogramG = new int[256];
        int[] histogramR = new int[256];
        int total = 0;
        for (int y = y0; y < y1; y += 2)
        {
            int row = y * stride;
            for (int x = x0; x < x1; x += 2)
            {
                int i = row + (x * 4);
                histogramB[pixels[i]]++;
                histogramG[pixels[i + 1]]++;
                histogramR[pixels[i + 2]]++;
                total++;
            }
        }

        if (total == 0)
        {
            return Colors.White;
        }

        return Color.FromRgb(
            HistogramMedian(histogramR, total),
            HistogramMedian(histogramG, total),
            HistogramMedian(histogramB, total));
    }

    private static byte HistogramMedian(ReadOnlySpan<int> histogram, int total)
    {
        int target = total / 2;
        int cumulative = 0;
        for (int i = 0; i < 256; i++)
        {
            cumulative += histogram[i];
            if (cumulative > target)
            {
                return (byte)i;
            }
        }

        return 255;
    }

    /// <summary>
    /// Median absolute deviation of the box interior from its own median, sampled on a
    /// 2-pixel grid. Glyph pixels are a minority, so this measures the surface
    /// flatness rather than the text contrast.
    /// </summary>
    private static int InteriorRoughness(
        ReadOnlySpan<byte> pixels,
        int stride,
        int x0,
        int y0,
        int x1,
        int y1,
        Color median)
    {
        int[] histogram = new int[256];
        int total = 0;
        for (int y = y0; y < y1; y += 2)
        {
            int row = y * stride;
            for (int x = x0; x < x1; x += 2)
            {
                int i = row + (x * 4);
                int delta = MaxChannelDelta(pixels[i], pixels[i + 1], pixels[i + 2], median);
                histogram[delta]++;
                total++;
            }
        }

        return total == 0 ? 0 : HistogramMedian(histogram, total);
    }

    /// <summary>Median absolute deviation of the ring samples (max channel, 0–255).</summary>
    private static int RingRoughness(IReadOnlyList<BackgroundSample> samples, Color median)
    {
        var deviations = new byte[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            deviations[i] = (byte)MaxChannelDelta(
                Color.FromRgb(samples[i].R, samples[i].G, samples[i].B),
                median);
        }

        Array.Sort(deviations);
        return deviations[deviations.Length / 2];
    }

    private static int MaxChannelDelta(Color a, Color b)
        => Math.Max(
            Math.Abs(a.B - b.B),
            Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.R - b.R)));

    private static int MaxChannelDelta(byte b, byte g, byte r, Color reference)
        => Math.Max(
            Math.Abs(b - reference.B),
            Math.Max(Math.Abs(g - reference.G), Math.Abs(r - reference.R)));

    /// <summary>WCAG-style relative luminance contrast ratio (1–21).</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        double lighter = Math.Max(la, lb);
        double darker = Math.Min(la, lb);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    private readonly struct MaskCandidate
    {
        public MaskCandidate(BackgroundPlane plane, Color background, bool usePlane)
        {
            Plane = plane;
            Background = background;
            UsePlane = usePlane;
        }

        public BackgroundPlane Plane { get; }

        public Color Background { get; }

        public bool UsePlane { get; }
    }

    private readonly struct EvaluatedCandidate
    {
        public EvaluatedCandidate(MaskCandidate candidate, byte[] distances, int threshold, bool plausible)
        {
            Candidate = candidate;
            Distances = distances;
            Threshold = threshold;
            Plausible = plausible;
        }

        public MaskCandidate Candidate { get; }

        public byte[] Distances { get; }

        public int Threshold { get; }

        public bool Plausible { get; }
    }
}
