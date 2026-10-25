// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Community.PowerToys.Run.Plugin.Snip.Models;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Renders recognized OCR lines to structured export formats. Both outputs keep
/// the line geometry so downstream tools can rebuild layout or diff runs.
/// </summary>
public static class OcrExport
{
    /// <summary>Supported structured export formats.</summary>
    public enum Format
    {
        /// <summary>GitHub-flavored Markdown table: #, text, confidence, position, size.</summary>
        Markdown = 0,

        /// <summary>JSON array of line objects (text, confidence, box, optional corners).</summary>
        Json = 1,
    }

    /// <summary>
    /// Renders lines to the requested format. Lines are sorted reading order
    /// (top-to-bottom, then left-to-right) as produced by the engines; geometry
    /// values are rounded to whole pixels.
    /// </summary>
    public static string Render(IReadOnlyList<OcrTextLine> lines, Format format)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return format switch
        {
            Format.Markdown => RenderMarkdown(lines),
            Format.Json => RenderJson(lines),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
    }

    private static string RenderMarkdown(IReadOnlyList<OcrTextLine> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| # | Text | Conf | X | Y | W | H |");
        sb.AppendLine("|---|------|------|---|---|---|---|");
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            sb.Append("| ").Append(i + 1).Append(" | ");
            AppendMarkdownCell(sb, l.Text);
            sb.Append(" | ").Append(FormatConfidence(l.Confidence));
            sb.Append(" | ").Append((int)Math.Round(l.Box.X));
            sb.Append(" | ").Append((int)Math.Round(l.Box.Y));
            sb.Append(" | ").Append((int)Math.Round(l.Box.Width));
            sb.Append(" | ").Append((int)Math.Round(l.Box.Height));
            sb.AppendLine(" |");
        }

        return sb.ToString();
    }

    private static string RenderJson(IReadOnlyList<OcrTextLine> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[");
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            sb.Append("  { \"text\": ").Append(QuoteJson(l.Text));
            sb.Append(", \"confidence\": ").Append(FormatConfidence(l.Confidence));
            sb.Append(", \"box\": { \"x\": ").Append((int)Math.Round(l.Box.X));
            sb.Append(", \"y\": ").Append((int)Math.Round(l.Box.Y));
            sb.Append(", \"w\": ").Append((int)Math.Round(l.Box.Width));
            sb.Append(", \"h\": ").Append((int)Math.Round(l.Box.Height));
            sb.Append(" }");

            if (l.Corners is { Count: 4 } corners)
            {
                sb.Append(", \"corners\": [");
                for (int c = 0; c < corners.Count; c++)
                {
                    if (c > 0)
                    {
                        sb.Append(", ");
                    }

                    sb.Append("{ \"x\": ").Append((int)Math.Round(corners[c].X))
                        .Append(", \"y\": ").Append((int)Math.Round(corners[c].Y)).Append(" }");
                }

                sb.Append(" ]");
            }

            sb.Append(" }");
            if (i < lines.Count - 1)
            {
                sb.Append(',');
            }

            sb.AppendLine();
        }

        sb.AppendLine("]");
        return sb.ToString();
    }

    private static void AppendMarkdownCell(StringBuilder sb, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Escape pipe characters and collapse newlines so the row stays a row.
        foreach (char c in text)
        {
            switch (c)
            {
                case '|':
                    sb.Append("\\|");
                    break;
                case '\r':
                    break;
                case '\n':
                    sb.Append("<br>");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
    }

    private static string QuoteJson(string text)
    {
        if (text is null)
        {
            return "null";
        }

        var sb = new StringBuilder(text.Length + 2);
        sb.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static string FormatConfidence(float confidence)
        => confidence is > 0 and <= 1
            ? confidence.ToString("0.00", CultureInfo.InvariantCulture)
            : "1.00";
}
