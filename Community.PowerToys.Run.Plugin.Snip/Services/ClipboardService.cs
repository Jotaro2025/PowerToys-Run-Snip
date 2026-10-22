// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Core;

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
            PutDib(bitmap);
            PutPng(bitmap, pngBytes);
        }
        finally
        {
            CloseClipboard();
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

    private static void PutDib(BitmapSource bitmap)
    {
        int w = bitmap.PixelWidth;
        int h = bitmap.PixelHeight;
        int stride = w * 4;
        int imageBytes = stride * h;
        int headerBytes = Marshal.SizeOf<BITMAPINFOHEADER>();
        int total = headerBytes + imageBytes;

        var pixels = new byte[imageBytes];
        bitmap.CopyPixels(pixels, stride, 0);

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
                var bmi = new BITMAPINFOHEADER
                {
                    BiSize = (uint)headerBytes,
                    BiWidth = w,
                    BiHeight = h,
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = 0,
                    BiSizeImage = (uint)imageBytes,
                };
                Marshal.StructureToPtr(bmi, pDib, false);

                // DIB rows are stored bottom-up; our pixel buffer is top-down.
                unsafe
                {
                    byte* dst = (byte*)pDib + headerBytes;
                    fixed (byte* src = pixels)
                    {
                        CopyTopDownBgraToBottomUpDib(src, dst, h, stride);
                    }
                }
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
            Marshal.Copy(pngBytes, 0, pPng, pngBytes.Length);
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
    /// Flip top-down BGRA into bottom-up DIB rows with <see cref="Buffer.MemoryCopy"/>,
    /// then clear the alpha byte (BI_RGB ignores it; keep high byte zero).
    /// </summary>
    internal static unsafe void CopyTopDownBgraToBottomUpDib(byte* src, byte* dst, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            byte* srcRow = src + ((height - 1 - y) * (long)stride);
            byte* dstRow = dst + (y * (long)stride);
            Buffer.MemoryCopy(srcRow, dstRow, stride, stride);
            for (int x = 3; x < stride; x += 4)
            {
                dstRow[x] = 0;
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
