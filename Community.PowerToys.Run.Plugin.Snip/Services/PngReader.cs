// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Lightweight PNG helpers for the OCR input path: header-only size probing
/// (8-byte signature + IHDR) and thread-pool-safe decoding into a frozen
/// <see cref="BitmapSource"/>.
/// </summary>
public static class PngReader
{
    /// <summary>
    /// Reads pixel dimensions from the PNG header without decoding, or null
    /// when the buffer is not a PNG (or too short to contain IHDR).
    /// </summary>
    public static (int Width, int Height)? TryReadSize(ReadOnlySpan<byte> png)
    {
        const int IHDRWidthOffset = 16;
        const int IHDRHeightOffset = 20;
        const int MinHeaderLength = 24;
        if (png.Length < MinHeaderLength
            || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G')
        {
            return null;
        }

        int width = ReadBigEndianInt32(png[IHDRWidthOffset..IHDRHeightOffset]);
        int height = ReadBigEndianInt32(png[IHDRHeightOffset..MinHeaderLength]);
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return (width, height);
    }

    /// <summary>
    /// Decodes PNG bytes into a frozen Bgra32 <see cref="BitmapSource"/> on the
    /// calling thread (no WPF control affinity needed). Null when the bytes are
    /// not a decodable image.
    /// </summary>
    public static BitmapSource DecodeFrozen(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        try
        {
            using var ms = new MemoryStream(png);
            var decoder = new PngBitmapDecoder(
                ms,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
            if (frame == null)
            {
                return null;
            }

            BitmapSource converted = frame.Format == System.Windows.Media.PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes)
        => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
}
