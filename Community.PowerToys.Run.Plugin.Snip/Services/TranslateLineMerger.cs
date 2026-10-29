// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;

using Community.PowerToys.Run.Plugin.Snip.Models;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Groups raw OCR line boxes into one box per visual line before translation.
/// OCR engines split a line into fragments (inline code, punctuation runs, single
/// characters) that land on the same baseline; translating each fragment in place
/// paints a stray one- or two-character glyph wherever a fragment sat, which reads
/// as "black dots" scattered over the translated image. Merging first also gives the
/// translator whole sentences instead of fragments.
/// </summary>
public static class TranslateLineMerger
{
    /// <summary>Minimum vertical overlap (fraction of the shorter box) to share a line.</summary>
    internal const double MinVerticalOverlap = 0.5;

    /// <summary>Horizontal gap allowed between fragments of the same line (× box height).</summary>
    internal const double MaxGapFactor = 0.75;

    /// <summary>Lower bound for the gap allowance, in source pixels.</summary>
    internal const double MinGapPixels = 4;

    /// <summary>
    /// Returns one line per visual line: punctuation-only fragments are dropped,
    /// overlapping duplicates are collapsed, and same-baseline neighbours are joined.
    /// </summary>
    public static IReadOnlyList<OcrTextLine> Merge(IReadOnlyList<OcrTextLine> lines)
    {
        if (lines == null || lines.Count == 0)
        {
            return lines ?? Array.Empty<OcrTextLine>();
        }

        var candidates = lines
            .Where(l => l != null && HasTranslatableText(l.Text) && l.Box.Width > 0 && l.Box.Height > 0)
            .OrderBy(l => l.Box.Top)
            .ThenBy(l => l.Box.Left)
            .ToList();

        if (candidates.Count <= 1)
        {
            return candidates;
        }

        var groups = new List<MergedLine>(candidates.Count);
        foreach (var line in candidates)
        {
            MergedLine target = null;
            foreach (var group in groups)
            {
                if (IsSameVisualLine(group.Box, line.Box))
                {
                    target = group;
                    break;
                }
            }

            if (target == null)
            {
                groups.Add(new MergedLine(line));
            }
            else
            {
                target.Merge(line);
            }
        }

        return groups
            .OrderBy(g => g.Box.Top)
            .ThenBy(g => g.Box.Left)
            .Select(g => new OcrTextLine(g.Text, g.Box, 1f))
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="text"/> carries words (letters, digits or CJK);
    /// punctuation-only OCR noise ("|", "·", "——") is not worth translating.
    /// </summary>
    internal static bool HasTranslatableText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Same baseline (vertical overlap) and close enough horizontally to belong to
    /// one visual line. Negative gaps (overlap) always qualify, which also collapses
    /// duplicate boxes reported for the same text.
    /// </summary>
    internal static bool IsSameVisualLine(System.Windows.Rect a, System.Windows.Rect b)
    {
        double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        double minHeight = Math.Min(a.Height, b.Height);
        if (minHeight <= 0 || overlap < minHeight * MinVerticalOverlap)
        {
            return false;
        }

        double gap = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right);
        double allowed = Math.Max(MinGapPixels, minHeight * MaxGapFactor);
        return gap <= allowed;
    }

    /// <summary>
    /// Joins fragment texts: a space only between two Latin word characters, so
    /// "NativeAOT" + "runs" keeps a gap while CJK runs stay tight ("中文" + "内容").
    /// </summary>
    internal static string JoinText(string left, string right)
    {
        if (string.IsNullOrEmpty(left))
        {
            return right ?? string.Empty;
        }

        if (string.IsNullOrEmpty(right))
        {
            return left;
        }

        char last = left[^1];
        char first = right[0];
        bool bothLatinWords = IsAsciiWordChar(last) && IsAsciiWordChar(first);
        return bothLatinWords ? left + " " + right : left + right;
    }

    private static bool IsAsciiWordChar(char c)
        => c < 128 && char.IsLetterOrDigit(c);

    private sealed class MergedLine
    {
        private readonly List<string> _parts = new();

        public MergedLine(OcrTextLine line)
        {
            Box = line.Box;
            _parts.Add(line.Text);
        }

        public System.Windows.Rect Box { get; private set; }

        public string Text => _parts.Aggregate(JoinText);

        public void Merge(OcrTextLine line)
        {
            Box = System.Windows.Rect.Union(Box, line.Box);

            // Some engines report the same box twice; do not translate it twice.
            if (!_parts.Contains(line.Text, StringComparer.Ordinal))
            {
                _parts.Add(line.Text);
            }
        }
    }
}
