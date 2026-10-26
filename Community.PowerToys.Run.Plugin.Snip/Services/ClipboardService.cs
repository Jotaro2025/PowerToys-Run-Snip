// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Clipboard helper. On Windows we put a DIB on the clipboard directly so
/// pasting into Paint / Word / Discord works, plus a PNG copy for modern apps.
/// </summary>
public static class ClipboardService
{
    private const int CfDib = 8;
    private const int MaxOpenAttempts = 5;

    /// <param name="pngBytes">
    /// Optional pre-encoded PNG (e.g. from <see cref="PngEncoder"/>). When
    /// non-empty, clipboard PNG is taken from this buffer — no second
    /// <see cref="PngBitmapEncoder"/> pass.
    /// </param>
    public static void CopyBitmap(BitmapSource bitmap, byte[] pngBytes = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!TryOpenClipboard())
        {
            return;
        }

        try
        {
            EmptyClipboard();
            try
            {
                PutDib(bitmap);
            }
            catch (Exception ex)
            {
                // DIB build must not abort the PNG placement — otherwise a failed
                // DIB (after EmptyClipboard already ran) leaves the clipboard empty.
                Log.Warn($"Snip clipboard DIB failed (PNG still placed): {ex.Message}", typeof(ClipboardService));
            }

            PutPng(bitmap, pngBytes);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Freezes (if needed), runs <see cref="BitmapSource.CopyPixels"/> + PNG encode
    /// off the UI thread for large frames, then applies CF_DIB/PNG on the caller
    /// sync context (typically STA/UI for clipboard ownership).
    /// </summary>
    public static async Task CopyBitmapAsync(
        BitmapSource bitmap,
        byte[] pngBytes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        BitmapSource frozen = bitmap;
        if (!frozen.IsFrozen)
        {
            frozen = bitmap.Clone();
            frozen.Freeze();
        }

        const int asyncPixelThreshold = 512 * 512;
        long pixels = (long)frozen.PixelWidth * frozen.PixelHeight;
        bool alreadyHavePng = pngBytes != null && pngBytes.Length > 0;
        if (pixels < asyncPixelThreshold && alreadyHavePng)
        {
            CopyBitmap(frozen, pngBytes);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var payload = await Task.Run(
            () => BuildClipboardPayload(frozen, pngBytes),
            cancellationToken).ConfigureAwait(true);

        try
        {
            ApplyClipboardPayload(payload);
        }
        finally
        {
            payload.Dispose();
        }
    }

    private sealed class ClipboardPayload : IDisposable
    {
        public byte[] RentedDib { get; set; }

        public int DibLength { get; init; }

        public byte[] Png { get; init; }

        public void Dispose()
        {
            if (RentedDib != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(RentedDib);
                RentedDib = null;
            }
        }
    }

    private static ClipboardPayload BuildClipboardPayload(BitmapSource bitmap, byte[] existingPng)
    {
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int stride = width * 4;
        int headerBytes = Marshal.SizeOf<BITMAPINFOHEADER>();
        int imageBytes = stride * height;
        int dibLength = headerBytes + imageBytes;
        byte[] dib = System.Buffers.ArrayPool<byte>.Shared.Rent(dibLength);

        try
        {
            var bmi = new BITMAPINFOHEADER
            {
                BiSize = (uint)headerBytes,
                BiWidth = width,
                BiHeight = height,
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = 0,
                BiSizeImage = (uint)imageBytes,
            };
            MemoryMarshal.Write(dib.AsSpan(0, headerBytes), in bmi);

            // One bulk CopyPixels into a pooled staging buffer, then flip rows into
            // the bottom-up DIB — thousands of per-row interop calls for a 4K
            // frame collapse into one interop transition + memcpys.
            byte[] staging = System.Buffers.ArrayPool<byte>.Shared.Rent(imageBytes);
            try
            {
                bitmap.CopyPixels(new Int32Rect(0, 0, width, height), staging, stride, 0);
                for (int y = 0; y < height; y++)
                {
                    int destOffset = headerBytes + (y * stride);
                    var dstRow = dib.AsSpan(destOffset, stride);
                    staging.AsSpan((height - 1 - y) * stride, stride).CopyTo(dstRow);
                    ClearBgraAlpha(dstRow);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(staging);
            }

            byte[] png;
            if (existingPng != null && existingPng.Length > 0)
            {
                png = existingPng;
            }
            else
            {
                png = PngEncoder.Encode(bitmap);
            }

            var payload = new ClipboardPayload
            {
                RentedDib = dib,
                DibLength = dibLength,
                Png = png,
            };
            dib = null; // ownership transferred to ClipboardPayload
            return payload;
        }
        finally
        {
            if (dib != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(dib);
            }
        }
    }

    private static void ApplyClipboardPayload(ClipboardPayload payload)
    {
        if (!TryOpenClipboard())
        {
            return;
        }

        try
        {
            EmptyClipboard();
            if (payload.RentedDib != null && payload.DibLength > 0)
            {
                SetHGlobalFormat(CfDib, payload.RentedDib.AsSpan(0, payload.DibLength));
            }

            if (payload.Png != null && payload.Png.Length > 0)
            {
                SetHGlobalFormat(RegisterClipboardFormat("PNG"), payload.Png.AsSpan());
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void SetHGlobalFormat(uint format, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        IntPtr hGlobal = GlobalAlloc(Win32.GmemMoveable, (uint)bytes.Length);
        if (hGlobal == IntPtr.Zero)
        {
            return;
        }

        IntPtr p = GlobalLock(hGlobal);
        if (p == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return;
        }

        try
        {
            unsafe
            {
                bytes.CopyTo(new Span<byte>((void*)p, bytes.Length));
            }
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Win32: place a DIB (plus PNG) on the clipboard so "Paste" in Paint / Word / Discord works.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// OpenClipboard fails while another window holds the clipboard open; retry briefly.
    /// </summary>
    private static bool TryOpenClipboard()
    {
        for (int attempt = 0; attempt < MaxOpenAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    /// <summary>
    /// Builds CF_DIB in the locked HGLOBAL: header + bottom-up BGRA rows.
    /// Rows are <see cref="BitmapSource.CopyPixels(Int32Rect, IntPtr, int, int)"/>'d
    /// straight into the GlobalLock buffer (flip while writing) and alpha-cleared
    /// via <see cref="Span{T}"/> — no intermediate full-frame <c>byte[]</c>.
    /// </summary>
    private static void PutDib(BitmapSource bitmap)
    {
        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        int stride = w * 4;
        int imageBytes = stride * h;
        int headerBytes = Marshal.SizeOf<BITMAPINFOHEADER>();
        int total = headerBytes + imageBytes;

        IntPtr hGlobal = GlobalAlloc(Win32.GmemMoveable, (uint)total);
        if (hGlobal == IntPtr.Zero)
        {
            return;
        }

        try
        {
            IntPtr pDib = GlobalLock(hGlobal);
            if (pDib == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
                return;
            }

            try
            {
                WriteDibIntoLockedBuffer(bitmap, pDib, w, h, stride, headerBytes, imageBytes);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CfDib, hGlobal) == IntPtr.Zero)
            {
                GlobalFree(hGlobal);
            }
        }
        catch
        {
            GlobalFree(hGlobal);
            throw;
        }
    }

    /// <summary>
    /// Writes BITMAPINFOHEADER + flipped BI_RGB pixels into an already-locked
    /// CF_DIB buffer (peak = HGLOBAL only).
    /// </summary>
    private static unsafe void WriteDibIntoLockedBuffer(
        BitmapSource bitmap,
        IntPtr pDib,
        int width,
        int height,
        int stride,
        int headerBytes,
        int imageBytes)
    {
        var bmi = new BITMAPINFOHEADER
        {
            BiSize = (uint)headerBytes,
            BiWidth = width,
            BiHeight = height,
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = 0,
            BiSizeImage = (uint)imageBytes,
        };

        // Header via MemoryMarshal — no StructureToPtr boxing path.
        MemoryMarshal.Write(new Span<byte>((void*)pDib, headerBytes), in bmi);

        byte* pixelsBase = (byte*)pDib + headerBytes;

        // One bulk CopyPixels into pooled staging, then flip rows into the locked
        // HGLOBAL (bottom-up DIB row y ← top-down source row height-1-y).
        byte[] staging = System.Buffers.ArrayPool<byte>.Shared.Rent(imageBytes);
        try
        {
            fixed (byte* stageBase = staging)
            {
                bitmap.CopyPixels(new Int32Rect(0, 0, width, height), (IntPtr)stageBase, imageBytes, stride);
                for (int y = 0; y < height; y++)
                {
                    byte* dstRow = pixelsBase + (y * (long)stride);
                    Buffer.MemoryCopy(
                        stageBase + ((long)(height - 1 - y) * stride),
                        dstRow,
                        stride,
                        stride);
                    ClearBgraAlpha(new Span<byte>(dstRow, stride));
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(staging);
        }
    }

    private static void PutPng(BitmapSource bitmap, byte[] existingPng)
    {
        byte[] pngBytes;
        if (existingPng != null && existingPng.Length > 0)
        {
            pngBytes = existingPng;
        }
        else
        {
            // No pre-encoded PNG — encode once (also avoids ToArray when possible).
            pngBytes = PngEncoder.Encode(bitmap);
        }

        IntPtr hGlobal = GlobalAlloc(Win32.GmemMoveable, (uint)pngBytes.Length);
        if (hGlobal == IntPtr.Zero)
        {
            return;
        }

        IntPtr pPng = GlobalLock(hGlobal);
        if (pPng == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return;
        }

        try
        {
            unsafe
            {
                pngBytes.AsSpan().CopyTo(new Span<byte>((void*)pPng, pngBytes.Length));
            }
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        if (SetClipboardData(RegisterClipboardFormat("PNG"), hGlobal) == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
        }
    }

    /// <summary>
    /// Flip top-down BGRA into bottom-up DIB rows via <see cref="Span{T}"/>,
    /// then clear the alpha byte (BI_RGB ignores it; keep high byte zero).
    /// Used by unit tests; production <see cref="PutDib"/> flips while
    /// CopyPixels-ing into the locked HGLOBAL.
    /// </summary>
    internal static void CopyTopDownBgraToBottomUpDib(
        ReadOnlySpan<byte> src,
        Span<byte> dst,
        int height,
        int stride)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stride);
        if (src.Length < height * stride || dst.Length < height * stride)
        {
            throw new ArgumentException("Buffer shorter than height * stride.");
        }

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> srcRow = src.Slice((height - 1 - y) * stride, stride);
            Span<byte> dstRow = dst.Slice(y * stride, stride);
            srcRow.CopyTo(dstRow);
            ClearBgraAlpha(dstRow);
        }
    }

    /// <summary>Zero every alpha byte in a BGRA row (BI_RGB high byte); 4-byte stride guaranteed by callers.</summary>
    internal static unsafe void ClearBgraAlpha(Span<byte> bgraRow)
    {
        // Cast to uint writes so the JIT can merge consecutive byte clears.
        int dwords = bgraRow.Length / 4;
        if (dwords > 0)
        {
            fixed (byte* p = bgraRow)
            {
                uint* v = (uint*)p;
                for (int i = 0; i < dwords; i++)
                {
                    v[i] &= 0x00FFFFFFu;
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint BiSize;

        public int BiWidth;

        public int BiHeight;

        public ushort BiPlanes;

        public ushort BiBitCount;

        public uint BiCompression;

        public uint BiSizeImage;

        public int BiXPelsPerMeter;

        public int BiYPelsPerMeter;

        public uint BiClrUsed;

        public uint BiClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, uint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
