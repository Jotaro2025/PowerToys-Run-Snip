// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Raw BGRA32 top-down screen pixels from GDI. Holds no WPF dispatcher object,
/// so BitBlt / GetDIBits can run on a thread-pool worker; call
/// <see cref="ToWriteableBitmap"/> on the UI STA thread to materialize the bitmap.
/// </summary>
public sealed class CapturedFrame
{
    public CapturedFrame(byte[] pixels, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame must have positive size.");
        }

        if (stride < width * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride too small for BGRA32.");
        }

        if (pixels.Length < stride * height)
        {
            throw new ArgumentException("Pixel buffer shorter than stride * height.", nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    /// <summary>Builds a 96-DPI BGRA <see cref="WriteableBitmap"/> (call on the UI thread).</summary>
    public WriteableBitmap ToWriteableBitmap()
    {
        var wb = new WriteableBitmap(
            Width,
            Height,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        wb.WritePixels(new Int32Rect(0, 0, Width, Height), Pixels, Stride, 0);
        return wb;
    }
}
