// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Encodes a <see cref="BitmapSource"/> to PNG bytes so callers do not have to
/// manage the encoder and stream themselves.
/// </summary>
public static class PngEncoder
{
    public static byte[] Encode(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return MaterializeBuffer(ms);
    }

    /// <summary>
    /// Freezes a snapshot on the calling (UI) thread, then PNG-encodes on the
    /// thread pool so large captures do not stall the STA dispatcher.
    /// </summary>
    public static Task<byte[]> EncodeAsync(BitmapSource bitmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        // Snapshot must happen on the bitmap's dispatcher thread (normally UI).
        BitmapSource frozen = FreezeSnapshot(bitmap);
        return Task.Run(() => Encode(frozen), cancellationToken);
    }

    /// <summary>
    /// Encodes PNG straight into <paramref name="stream"/> (no intermediate
    /// <c>byte[]</c>). Caller owns the stream.
    /// </summary>
    public static void EncodeToStream(BitmapSource bitmap, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(stream);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    /// <summary>
    /// Freeze on the calling thread, then write a PNG file on the thread pool
    /// without building a full extra <c>byte[]</c> via <see cref="MemoryStream.ToArray"/>.
    /// </summary>
    public static Task EncodeToFileAsync(
        BitmapSource bitmap,
        string path,
        CancellationToken cancellationToken = default)
        => EncodeToFileAsync(bitmap, path, FormatFromPath(path), cancellationToken);

    /// <summary>Encodes to a file; format from extension (.png default, .jpg/.jpeg, .bmp).</summary>
    public static Task EncodeToFileAsync(
        BitmapSource bitmap,
        string path,
        ImageFileFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        BitmapSource frozen = FreezeSnapshot(bitmap);
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var fs = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous);
                EncodeToStream(frozen, fs, format);
                fs.Flush();
            },
            cancellationToken);
    }

    /// <summary>Maps a file path extension to <see cref="ImageFileFormat"/> (default PNG).</summary>
    public static ImageFileFormat FormatFromPath(string path)
    {
        var ext = Path.GetExtension(path)?.ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => ImageFileFormat.Jpeg,
            ".bmp" => ImageFileFormat.Bmp,
            _ => ImageFileFormat.Png,
        };
    }

    public static void EncodeToStream(BitmapSource bitmap, Stream stream, ImageFileFormat format)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(stream);

        BitmapEncoder encoder = format switch
        {
            ImageFileFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = 92 },
            ImageFileFormat.Bmp => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    /// <summary>
    /// Prefer <see cref="MemoryStream.TryGetBuffer"/> so we only allocate an
    /// exact-sized array when the expandable buffer is larger than <c>Length</c>
    /// (avoids an opaque <see cref="MemoryStream.ToArray"/> when possible).
    /// </summary>
    internal static byte[] MaterializeBuffer(MemoryStream ms)
    {
        ArgumentNullException.ThrowIfNull(ms);

        if (ms.TryGetBuffer(out ArraySegment<byte> segment))
        {
            if (segment.Array != null
                && segment.Offset == 0
                && segment.Count == segment.Array.Length)
            {
                // Internal buffer is already exact-sized — hand it off.
                return segment.Array;
            }

            var exact = new byte[segment.Count];
            if (segment.Count > 0)
            {
                Buffer.BlockCopy(segment.Array!, segment.Offset, exact, 0, segment.Count);
            }

            return exact;
        }

        return ms.ToArray();
    }

    private static BitmapSource FreezeSnapshot(BitmapSource bitmap)
    {
        if (bitmap.IsFrozen)
        {
            return bitmap;
        }

        var frozen = bitmap.Clone();
        frozen.Freeze();
        return frozen;
    }
}

/// <summary>On-disk image formats supported by the save dialog (PNG remains default).</summary>
public enum ImageFileFormat
{
    Png,
    Jpeg,
    Bmp,
}
