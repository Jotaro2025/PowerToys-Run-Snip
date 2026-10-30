// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Media;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Glyph mask plus the original text style for one OCR box. Built from a local
/// background model (ring plane, interior median, or ring median) so the renderer can
/// erase only strokes, keep the real background, and repaint in the source color.
/// </summary>
internal sealed class TextMaskResult
{
    public byte[] Mask { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int Left { get; init; }

    public int Top { get; init; }

    /// <summary>Local background color (interior plate color when a plate was detected).</summary>
    public Color Background { get; init; }

    public Color Foreground { get; init; }

    public bool Bold { get; init; }

    /// <summary>Fraction of box pixels that are glyph ink (before dilation).</summary>
    public double InkFraction { get; init; }

    /// <summary>True when the box sits on a plate (pill/code block) with its own flat color.</summary>
    public bool HasPlate { get; init; }

    public BackgroundPlane Plane { get; init; }

    public MaskFillKind FillKind { get; init; }

    /// <summary>Telea sampling radius tuned to the measured stroke width.</summary>
    public int InpaintRadius { get; init; }

    public int MaskPixelCount { get; init; }

    /// <summary>Integer bounds of the erased pixels in image coordinates.</summary>
    public Int32Rect Bounds { get; init; }

    /// <summary>True when the mask is a plausible text mask and can replace box erase.</summary>
    public bool IsUsable { get; init; }

    /// <summary>Fills every masked pixel with the plate/flat background color.</summary>
    public void FillSolid(Span<byte> pixels, int stride, int width, int height)
    {
        for (int y = 0; y < Height; y++)
        {
            int py = Top + y;
            if (py < 0 || py >= height)
            {
                continue;
            }

            int maskRow = y * Width;
            int pixelRow = py * stride;
            for (int x = 0; x < Width; x++)
            {
                if (Mask[maskRow + x] == 0)
                {
                    continue;
                }

                int px = Left + x;
                if (px < 0 || px >= width)
                {
                    continue;
                }

                int i = pixelRow + (px * 4);
                pixels[i] = Background.B;
                pixels[i + 1] = Background.G;
                pixels[i + 2] = Background.R;
                pixels[i + 3] = 255;
            }
        }
    }

    /// <summary>Fills every masked pixel with the fitted plane prediction.</summary>
    public bool FillWithPlane(Span<byte> pixels, int stride, int width, int height)
        => Plane != null
            && Plane.FillMask(pixels, stride, width, height, Mask, Width, Height, Left, Top);
}
