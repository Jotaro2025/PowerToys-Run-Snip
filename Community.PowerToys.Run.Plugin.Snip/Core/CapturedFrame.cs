// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Screen pixels from GDI. Production capture owns a top-down BGRA
/// <c>CreateDIBSection</c> HBITMAP across the thread-pool → UI boundary (no
/// full-frame managed <c>byte[]</c>). Call <see cref="ToWriteableBitmap"/> on
/// the UI STA to Span-copy into <see cref="WriteableBitmap.BackBuffer"/> then
/// release the DIB. A managed-buffer constructor remains for unit tests.
/// </summary>
public sealed class CapturedFrame : IDisposable
{
    private IntPtr _dib;
    private IntPtr _bits;
    private byte[] _pixels;
    private bool _disposed;

    /// <summary>
    /// Takes ownership of a GDI DIB section. <paramref name="bits"/> stays
    /// valid until <see cref="Dispose"/> / <see cref="ToWriteableBitmap"/>.
    /// Caller must have unselected the HBITMAP from any DC.
    /// </summary>
    internal CapturedFrame(IntPtr dib, IntPtr bits, int width, int height, int stride)
    {
        if (dib == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(dib));
        }

        if (bits == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(bits));
        }

        ValidateSize(width, height, stride);

        _dib = dib;
        _bits = bits;
        Width = width;
        Height = height;
        Stride = stride;
    }

    /// <summary>Managed BGRA32 buffer (tests / no GDI). Does not allocate extra copies.</summary>
    public CapturedFrame(byte[] pixels, int width, int height, int stride)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ValidateSize(width, height, stride);

        if (pixels.Length < stride * height)
        {
            throw new ArgumentException("Pixel buffer shorter than stride * height.", nameof(pixels));
        }

        _pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    /// <summary>
    /// True when this frame still owns a GDI DIB (production capture path).
    /// </summary>
    internal bool OwnsNativeDib => _dib != IntPtr.Zero;

    /// <summary>
    /// Builds a 96-DPI BGRA <see cref="WriteableBitmap"/> (call on the UI thread).
    /// Copies via <see cref="Span{T}"/> into <see cref="WriteableBitmap.BackBuffer"/>
    /// when a native DIB is held, then releases the HBITMAP (no mid-frame
    /// <c>byte[]</c>). Idempotent with <see cref="Dispose"/>.
    /// </summary>
    public WriteableBitmap ToWriteableBitmap()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var wb = new WriteableBitmap(
            Width,
            Height,
            96,
            96,
            PixelFormats.Bgra32,
            null);

        try
        {
            int byteCount = CaptureBuffers.BgraByteCount(Width, Height);
            wb.Lock();
            try
            {
                if (wb.BackBufferStride != Stride)
                {
                    throw new InvalidOperationException(
                        $"WriteableBitmap stride {wb.BackBufferStride} != frame stride {Stride}.");
                }

                if (_bits != IntPtr.Zero)
                {
                    CopyNativeBitsToBackBuffer(wb.BackBuffer, byteCount);
                }
                else
                {
                    ArgumentNullException.ThrowIfNull(_pixels);
                    _pixels.AsSpan(0, byteCount).CopyTo(BackBufferSpan(wb.BackBuffer, byteCount));
                }

                wb.AddDirtyRect(new Int32Rect(0, 0, Width, Height));
            }
            finally
            {
                wb.Unlock();
            }

            return wb;
        }
        finally
        {
            // Release DIB immediately after the copy attempt so peak stays
            // DIB+WB only briefly; never leave bits usable after Dispose.
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_dib != IntPtr.Zero)
        {
            // Clear bits before DeleteObject so a racy reader cannot observe
            // a non-zero bits pointer after the HBITMAP is gone.
            _bits = IntPtr.Zero;
            _ = Win32.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        _pixels = null;
        GC.SuppressFinalize(this);
    }

    ~CapturedFrame()
    {
        // Best-effort GDI cleanup if ToWriteableBitmap / Dispose was skipped.
        if (_dib != IntPtr.Zero)
        {
            _bits = IntPtr.Zero;
            _ = Win32.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
    }

    private unsafe void CopyNativeBitsToBackBuffer(IntPtr backBuffer, int byteCount)
    {
        // Snapshot bits under the same rules as Dispose: only use while _dib lives.
        ObjectDisposedException.ThrowIf(_bits == IntPtr.Zero || _dib == IntPtr.Zero, this);

        IntPtr bits = _bits;
        var src = new ReadOnlySpan<byte>((void*)bits, byteCount);
        var dst = new Span<byte>((void*)backBuffer, byteCount);
        src.CopyTo(dst);
    }

    private static unsafe Span<byte> BackBufferSpan(IntPtr backBuffer, int byteCount)
        => new Span<byte>((void*)backBuffer, byteCount);

    private static void ValidateSize(int width, int height, int stride)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame must have positive size.");
        }

        if (stride < width * 4)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), "Stride too small for BGRA32.");
        }
    }
}
