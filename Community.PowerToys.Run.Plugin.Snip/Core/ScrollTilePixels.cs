// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Owned BGRA32 tile buffer for scroll capture. Lives on the thread pool;
/// convert to <see cref="WriteableBitmap"/> only on the UI STA via
/// <see cref="ToWriteableBitmap"/>.
/// </summary>
public sealed class ScrollTilePixels : IDisposable
{
    private byte[] _pixels;
    private bool _rented;
    private bool _disposed;

    public ScrollTilePixels(byte[] pixels, int width, int height, int stride, bool rentedFromPool = false)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);

        int needed = checked(stride * height);
        if (pixels.Length < needed)
        {
            throw new ArgumentException("Pixel buffer shorter than stride * height.", nameof(pixels));
        }

        _pixels = pixels;
        _rented = rentedFromPool;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public ReadOnlySpan<byte> Pixels
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pixels.AsSpan(0, Stride * Height);
        }
    }

    internal byte[] DangerousGetArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pixels;
    }

    /// <summary>UI STA only — copies into a new WriteableBitmap.</summary>
    public WriteableBitmap ToWriteableBitmap()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int w = Width;
        int h = Height;
        var wb = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        wb.Lock();
        try
        {
            int byteCount = CaptureBuffers.BgraByteCount(w, h);
            if (wb.BackBufferStride != Stride)
            {
                // Row-copy when WB stride differs (rare; usually equal for BGRA32).
                unsafe
                {
                    byte* dst = (byte*)wb.BackBuffer;
                    int dstStride = wb.BackBufferStride;
                    int rowBytes = w * 4;
                    for (int y = 0; y < h; y++)
                    {
                        _pixels.AsSpan(y * Stride, rowBytes)
                            .CopyTo(new Span<byte>(dst + ((long)y * dstStride), rowBytes));
                    }
                }
            }
            else
            {
                unsafe
                {
                    _pixels.AsSpan(0, byteCount)
                        .CopyTo(new Span<byte>((void*)wb.BackBuffer, byteCount));
                }
            }

            wb.AddDirtyRect(new Int32Rect(0, 0, w, h));
        }
        finally
        {
            wb.Unlock();
        }

        return wb;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_rented && _pixels != null)
        {
            ArrayPool<byte>.Shared.Return(_pixels);
        }

        _pixels = null;
    }
}
