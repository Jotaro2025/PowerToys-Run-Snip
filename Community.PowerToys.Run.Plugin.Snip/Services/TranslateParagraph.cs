// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Models;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// One visual paragraph: consecutive OCR lines that share a column and line pitch.
/// Reflowing the translation into the paragraph's union box keeps long translations
/// (ZH→EN typically doubles in length) readable instead of shrinking every line into
/// its own tight box.
/// </summary>
internal sealed class TranslateParagraph
{
    private TranslateParagraph(
        IReadOnlyList<int> lineIndices,
        Rect box,
        double lineHeight,
        ParagraphAlignment alignment)
    {
        LineIndices = lineIndices;
        Box = box;
        LineHeight = lineHeight;
        Alignment = alignment;
    }

    public IReadOnlyList<int> LineIndices { get; }

    /// <summary>Union of the member line boxes.</summary>
    public Rect Box { get; }

    /// <summary>Tallest member line box (proxy for the original font size).</summary>
    public double LineHeight { get; }

    public ParagraphAlignment Alignment { get; }

    public bool IsSingleLine => LineIndices.Count == 1;

    /// <summary>
    /// Groups lines into paragraphs. Rotated (quad) lines always stay single-line, and
    /// lines must share a column, a similar height and a tight vertical gap to merge,
    /// so side-by-side UI columns never collapse into one paragraph.
    /// </summary>
    public static IReadOnlyList<TranslateParagraph> Group(IReadOnlyList<OcrTextLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var entries = new List<Entry>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line == null || line.Box.Width <= 0 || line.Box.Height <= 0)
            {
                continue;
            }

            bool isQuad = ImageTranslateRenderer.IsUsableQuad(line.Corners, out _, out _);
            entries.Add(new Entry(i, line.Box, isQuad));
        }

        entries.Sort(static (a, b) =>
        {
            int byTop = a.Box.Top.CompareTo(b.Box.Top);
            return byTop != 0 ? byTop : a.Box.Left.CompareTo(b.Box.Left);
        });

        var groups = new List<List<Entry>>();
        foreach (var entry in entries)
        {
            if (entry.IsQuad)
            {
                groups.Add([entry]);
                continue;
            }

            List<Entry> best = null;
            double bestGap = double.MaxValue;
            foreach (var group in groups)
            {
                var last = group[^1];
                if (last.IsQuad || !IsCompatible(last.Box, entry.Box, out double gap))
                {
                    continue;
                }

                if (gap < bestGap)
                {
                    best = group;
                    bestGap = gap;
                }
            }

            if (best == null)
            {
                groups.Add([entry]);
            }
            else
            {
                best.Add(entry);
            }
        }

        return groups
            .Select(Build)
            .OrderBy(p => p.Box.Top)
            .ThenBy(p => p.Box.Left)
            .ToList();
    }

    private static TranslateParagraph Build(List<Entry> group)
    {
        Rect box = group[0].Box;
        double lineHeight = 0;
        var indices = new int[group.Count];
        for (int i = 0; i < group.Count; i++)
        {
            box = Rect.Union(box, group[i].Box);
            lineHeight = Math.Max(lineHeight, group[i].Box.Height);
            indices[i] = group[i].Index;
        }

        Array.Sort(indices);
        return new TranslateParagraph(indices, box, lineHeight, InferAlignment(group, box));
    }

    private static ParagraphAlignment InferAlignment(List<Entry> group, Rect box)
    {
        if (group.Count < 2 || box.Width <= 0)
        {
            return ParagraphAlignment.Left;
        }

        double tolerance = Math.Max(1.5, box.Width * 0.08);
        double minLeft = double.MaxValue, maxLeft = double.MinValue;
        double minCenter = double.MaxValue, maxCenter = double.MinValue;
        double minRight = double.MaxValue, maxRight = double.MinValue;
        foreach (var entry in group)
        {
            var b = entry.Box;
            minLeft = Math.Min(minLeft, b.Left);
            maxLeft = Math.Max(maxLeft, b.Left);
            minRight = Math.Min(minRight, b.Right);
            maxRight = Math.Max(maxRight, b.Right);
            minCenter = Math.Min(minCenter, b.Left + (b.Width / 2));
            maxCenter = Math.Max(maxCenter, b.Left + (b.Width / 2));
        }

        if (maxLeft - minLeft <= tolerance)
        {
            return ParagraphAlignment.Left;
        }

        if (maxRight - minRight <= tolerance)
        {
            return ParagraphAlignment.Right;
        }

        // Left and right edges both differ but the centers agree: centered block.
        if (maxCenter - minCenter <= tolerance)
        {
            return ParagraphAlignment.Center;
        }

        return ParagraphAlignment.Left;
    }

    /// <summary>
    /// Same column, similar glyph height and one line pitch apart. Negative gaps are
    /// allowed (boxes may overlap slightly) but a gap wider than 0.8× the line height
    /// starts a new paragraph.
    /// </summary>
    private static bool IsCompatible(Rect last, Rect next, out double gap)
    {
        gap = next.Top - last.Bottom;
        double minHeight = Math.Min(last.Height, next.Height);
        double maxHeight = Math.Max(last.Height, next.Height);
        if (minHeight <= 0 || maxHeight <= 0 || maxHeight > minHeight * 1.35)
        {
            return false;
        }

        if (gap < -minHeight * 0.35 || gap > Math.Max(3, minHeight * 0.8))
        {
            return false;
        }

        double overlap = Math.Min(last.Right, next.Right) - Math.Max(last.Left, next.Left);
        return overlap >= Math.Min(last.Width, next.Width) * 0.3;
    }

    private readonly struct Entry
    {
        public Entry(int index, Rect box, bool isQuad)
        {
            Index = index;
            Box = box;
            IsQuad = isQuad;
        }

        public int Index { get; }

        public Rect Box { get; }

        public bool IsQuad { get; }
    }
}
