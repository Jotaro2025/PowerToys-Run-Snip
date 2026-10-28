// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Converts Windows.Graphics.Capture FP16 (R16G16B16A16_FLOAT) frames from the
/// HDR composition surface to SDR BGRA32. The surface holds scene-referred
/// scRGB (linear, 1.0 = 80 nits); SDR content is scaled by
/// <c>SDR white / 80 nits</c>. Dividing that scale back out and applying the
/// sRGB transfer function restores the SDR colors an 8-bit capture would have
/// produced before the HDR composition clipped them.
/// </summary>
internal static class HdrToneMapper
{
    /// <summary>Linear-light LUT resolution; 4096 steps keep rounding within one 8-bit level.</summary>
    internal const int LutSize = 4096;

    private const double SrgbLinearThreshold = 0.0031308;
    private const double SrgbSlope = 12.92;
    private const double SrgbOffset = 0.055;
    private const double SrgbScale = 1.055;
    private const double SrgbGamma = 2.4;

    /// <summary>
    /// Converts one FP16 RGBA frame (row-major, 8 bytes per pixel) into BGRA32.
    /// <paramref name="whiteScale"/> is 80 nits divided by the display's current
    /// SDR white level (1.0 for SDR / 80-nit HDR).
    /// </summary>
    public static void ConvertRgba16ToBgra8(
        ReadOnlySpan<byte> source,
        int sourceStride,
        Span<byte> destination,
        int width,
        int height,
        double whiteScale)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame must have positive size.");
        }

        if (sourceStride < width * 8)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStride), "Source stride too small for RGBA16.");
        }

        if (source.Length < sourceStride * height)
        {
            throw new ArgumentException("Source buffer shorter than stride * height.", nameof(source));
        }

        if (destination.Length < width * height * 4)
        {
            throw new ArgumentException("Destination buffer shorter than width * height * 4.", nameof(destination));
        }

        double scale = double.IsNaN(whiteScale) || whiteScale <= 0 ? 1.0 : Math.Min(whiteScale, 1.0);

        // The LUT spans the scRGB range that maps to [0, 1] after scaling
        // (SDR white and above). Values beyond it are HDR highlights → 255.
        double maxInput = Math.Max(1.0, 1.0 / scale);
        float indexScale = (float)((LutSize - 1) / maxInput);
        byte[] lut = BuildLut(scale, maxInput);

        var halves = MemoryMarshal.Cast<byte, ushort>(source);
        int sourceStrideShorts = sourceStride / 2;
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * sourceStrideShorts;
            int destinationOffset = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int pixel = sourceRow + (x * 4);
                float r = (float)BitConverter.UInt16BitsToHalf(halves[pixel]);
                float g = (float)BitConverter.UInt16BitsToHalf(halves[pixel + 1]);
                float b = (float)BitConverter.UInt16BitsToHalf(halves[pixel + 2]);

                destination[destinationOffset] = Lookup(lut, b * indexScale);
                destination[destinationOffset + 1] = Lookup(lut, g * indexScale);
                destination[destinationOffset + 2] = Lookup(lut, r * indexScale);
                destination[destinationOffset + 3] = 255;
                destinationOffset += 4;
            }
        }
    }

    /// <summary>
    /// sRGB transfer function for one linear-light channel in [0, 1].
    /// </summary>
    public static double EncodeToSrgb(double linear)
    {
        if (double.IsNaN(linear) || linear <= 0)
        {
            return 0;
        }

        if (linear >= 1)
        {
            return 1;
        }

        return linear <= SrgbLinearThreshold
            ? SrgbSlope * linear
            : (SrgbScale * Math.Pow(linear, 1.0 / SrgbGamma)) - SrgbOffset;
    }

    /// <summary>8-bit sRGB value for one linear-light channel.</summary>
    public static byte EncodeToSrgbByte(double linear)
        => (byte)Math.Round(EncodeToSrgb(linear) * 255.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// LUT over raw scRGB values [0, maxInput]; each entry maps
    /// <c>sRGB(clamp(raw * scale))</c> to a byte.
    /// </summary>
    internal static byte[] BuildLut(double scale, double maxInput)
    {
        var lut = new byte[LutSize];
        for (int i = 0; i < LutSize; i++)
        {
            double raw = (i / (double)(LutSize - 1)) * maxInput;
            lut[i] = EncodeToSrgbByte(raw * scale);
        }

        return lut;
    }

    private static byte Lookup(byte[] lut, float index)
    {
        // NaN/negative → 0; past the end (HDR highlights) → 255.
        if (!(index > 0))
        {
            return 0;
        }

        int i = (int)index;
        return i >= LutSize ? (byte)255 : lut[i];
    }
}
