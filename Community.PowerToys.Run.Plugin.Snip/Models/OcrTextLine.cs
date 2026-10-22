// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Models;

/// <summary>One OCR line with its axis-aligned box in image pixel space.</summary>
public sealed class OcrTextLine
{
    public OcrTextLine(string text, Rect box, float confidence)
    {
        Text = text ?? string.Empty;
        Box = box;
        Confidence = confidence;
    }

    public string Text { get; }

    /// <summary>Axis-aligned box covering the line (from Left/Top/Right/Bottom).</summary>
    public Rect Box { get; }

    public float Confidence { get; }
}
