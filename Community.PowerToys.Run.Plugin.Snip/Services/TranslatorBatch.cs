// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Numbered-line batching for multi-segment translation: one prompt → N aligned replies.
/// </summary>
internal static partial class TranslatorBatch
{
    /// <summary>HTTP OpenAI-compatible: larger batches (one round-trip is expensive).</summary>
    public const int HttpMaxLines = 20;

    public const int HttpMaxChars = 3500;

    /// <summary>HyMT2Sharp in-process: smaller chunks (context / generation cost).</summary>
    public const int HyMtMaxLines = 8;

    public const int HyMtMaxChars = 1500;

    [GeneratedRegex(@"^\s*(\d+)\s*[\.\)\:\、]\s*(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedLineRegex();

    /// <summary>
    /// Builds a prompt that asks for numbered translations matching 1..N input lines (already non-empty).
    /// </summary>
    public static string BuildNumberedPrompt(string targetLanguage, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            throw new ArgumentException("Target language is required.", nameof(targetLanguage));
        }

        if (lines.Count == 0)
        {
            throw new ArgumentException("At least one line is required.", nameof(lines));
        }

        var sb = new StringBuilder();
        sb.Append("Translate each of the following numbered lines into ")
            .Append(targetLanguage)
            .Append(", keeping the same numbers and order. Reply with one numbered line per input, without additional explanation：")
            .AppendLine();
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(i + 1)
                .Append(". ")
                .Append(lines[i] ?? string.Empty)
                .AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Tries to parse a numbered reply into <paramref name="expectedCount"/> strings (1-based indices).
    /// Returns false when alignment cannot be recovered safely.
    /// </summary>
    public static bool TryParseNumberedReply(string reply, int expectedCount, out string[] translations)
    {
        translations = null;
        if (expectedCount <= 0 || string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        var map = new Dictionary<int, string>();

        // EnumerateLines handles \r\n / \n / \r without Replace+Split allocations.
        foreach (var rawLine in reply.AsSpan().EnumerateLines())
        {
            // Regex group capture still needs a string; per-line alloc beats whole-reply Replace+Split.
            var m = NumberedLineRegex().Match(rawLine.ToString());
            if (!m.Success)
            {
                continue;
            }

            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                continue;
            }

            if (n < 1 || n > expectedCount)
            {
                continue;
            }

            // First wins if the model repeats a number.
            map.TryAdd(n, m.Groups[2].Value.Trim());
        }

        if (map.Count != expectedCount)
        {
            return false;
        }

        translations = new string[expectedCount];
        for (int i = 1; i <= expectedCount; i++)
        {
            if (!map.TryGetValue(i, out var t))
            {
                return false;
            }

            translations[i - 1] = t;
        }

        return true;
    }

    /// <summary>
    /// Splits non-empty work items into chunks limited by line count and total character size.
    /// Each item is (originalIndex, text).
    /// </summary>
    public static List<List<(int Index, string Text)>> Chunk(
        IReadOnlyList<(int Index, string Text)> items,
        int maxLines,
        int maxChars)
    {
        ArgumentNullException.ThrowIfNull(items);
        var chunks = new List<List<(int Index, string Text)>>();
        if (items.Count == 0)
        {
            return chunks;
        }

        List<(int Index, string Text)> current = null;
        int currentChars = 0;
        foreach (var item in items)
        {
            int len = item.Text?.Length ?? 0;
            bool needNew = current == null
                || current.Count >= maxLines
                || (current.Count > 0 && currentChars + len > maxChars);
            if (needNew)
            {
                current = new List<(int Index, string Text)>();
                chunks.Add(current);
                currentChars = 0;
            }

            current.Add(item);
            currentChars += len;
        }

        return chunks;
    }

    /// <summary>Collects non-empty (index, text) pairs from a source list.</summary>
    public static List<(int Index, string Text)> CollectNonEmpty(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var list = new List<(int Index, string Text)>(texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            var t = texts[i] ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(t))
            {
                list.Add((i, t));
            }
        }

        return list;
    }
}
