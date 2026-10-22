// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Models;

/// <summary>
/// One OCR line with its axis-aligned box (and optional corner quad) in image pixel space.
/// </summary>
public sealed class OcrTextLine
{
    public OcrTextLine(string text, Rect box, float confidence, IReadOnlyList<Point> corners = null)
    {
        Text = text ?? string.Empty;
        Box = box;
        Confidence = confidence;
        if (corners != null && corners.Count >= 4)
        {
            Corners =
            [
                corners[0],
                corners[1],
                corners[2],
                corners[3],
            ];
        }
    }

    public string Text { get; }

    /// <summary>Axis-aligned box covering the line (from Left/Top/Right/Bottom).</summary>
    public Rect Box { get; }

    public float Confidence { get; }

    /// <summary>
    /// Optional four corners from WeChat <c>SinglePos</c>, typically TL → TR → BR → BL.
    /// Null when unavailable; renderer then uses <see cref="Box"/>.
    /// </summary>
    public IReadOnlyList<Point> Corners { get; }
}
