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
        return ms.ToArray();
    }

    /// <summary>
    /// Freezes a snapshot on the calling (UI) thread, then PNG-encodes on the
    /// thread pool so large captures do not stall the STA dispatcher.
    /// </summary>
    public static Task<byte[]> EncodeAsync(BitmapSource bitmap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        // Snapshot must happen on the bitmap's dispatcher thread (normally UI).
        BitmapSource frozen;
        if (bitmap.IsFrozen)
        {
            frozen = bitmap;
        }
        else
        {
            frozen = bitmap.Clone();
            frozen.Freeze();
        }

        return Task.Run(() => Encode(frozen), cancellationToken);
    }
}
