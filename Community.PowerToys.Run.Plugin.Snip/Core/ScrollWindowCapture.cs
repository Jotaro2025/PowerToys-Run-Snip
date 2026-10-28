// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Runtime.InteropServices;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Window-local scroll tile capture so the snip overlay can stay visible.
/// Tries WinRT <c>CreateForWindow</c>, then GDI <c>PrintWindow</c>
/// (<c>PW_RENDERFULLCONTENT</c>), cropped to the physical selection rect.
/// Caller falls back to session hide + desktop BitBlt on failure (Edge safety).
/// </summary>
internal static class ScrollWindowCapture
{
    /// <summary>
    /// Resolve the top-level underlay HWND at a physical screen point (skipping the overlay).
    /// </summary>
    public static IntPtr ResolveUnderlayTopLevel(int physicalX, int physicalY, IntPtr overlayHwnd)
    {
        var pt = new Win32.POINT { X = physicalX, Y = physicalY };
        IntPtr target = Win32CaptureService.ResolveScrollTargetAt(pt, overlayHwnd);
        if (target == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr root = Win32.GetAncestor(target, Win32.GaRoot);
        return root != IntPtr.Zero ? root : target;
    }

    /// <summary>
    /// Capture a physical screen rect from <paramref name="windowHwnd"/> without reading the
    /// desktop composite (overlay may remain visible).
    /// </summary>
    public static bool TryCaptureRect(
        IntPtr windowHwnd,
        int physicalX,
        int physicalY,
        int width,
        int height,
        out CapturedFrame frame)
    {
        frame = null;
        if (windowHwnd == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return false;
        }

        if (!Win32.GetWindowRect(windowHwnd, out Win32.RECT wr))
        {
            return false;
        }

        int cropX = physicalX - wr.Left;
        int cropY = physicalY - wr.Top;
        int winW = wr.Width;
        int winH = wr.Height;
        if (winW <= 0 || winH <= 0)
        {
            return false;
        }

        // Selection must lie mostly inside the window; 1px slack for DPI rounding.
        if (cropX < -1 || cropY < -1 || cropX + width > winW + 1 || cropY + height > winH + 1)
        {
            Log.Info(
                $"Snip scroll window-capture: rect outside window bounds crop=({cropX},{cropY},{width}x{height}) win={winW}x{winH}",
                typeof(ScrollWindowCapture));
            return false;
        }

        cropX = Math.Clamp(cropX, 0, Math.Max(0, winW - width));
        cropY = Math.Clamp(cropY, 0, Math.Max(0, winH - height));

        if (TryCaptureViaWinRt(windowHwnd, cropX, cropY, width, height, out frame))
        {
            return true;
        }

        // PrintWindow renders through the 8-bit composition on HDR desktops and
        // would return washed-out tiles; let the caller use the HDR-aware
        // per-monitor capture fallback instead.
        if (WinRtGraphicsCapture.NeedsHdrToneMapping(Win32.MonitorFromWindow(windowHwnd, Win32.MonitorDefaultToNearest)))
        {
            Log.Info(
                "Snip scroll PrintWindow skipped on HDR monitor — using desktop HDR fallback",
                typeof(ScrollWindowCapture));
            return false;
        }

        if (TryCaptureViaPrintWindow(windowHwnd, winW, winH, cropX, cropY, width, height, out frame))
        {
            return true;
        }

        return false;
    }

    /// <summary>Pure BGRA crop into a newly allocated buffer (unit tests).</summary>
    internal static byte[] CropBgraBuffer(
        ReadOnlySpan<byte> src,
        int srcWidth,
        int srcHeight,
        int srcStride,
        int cropX,
        int cropY,
        int cropW,
        int cropH)
    {
        int dstBytes = ValidateAndComputeCropBytes(srcWidth, srcHeight, cropX, cropY, cropW, cropH);
        var dst = new byte[dstBytes];
        CropBgraBufferInto(src, srcStride, cropX, cropY, cropW, cropH, dst.AsSpan(0, dstBytes));
        return dst;
    }

    /// <summary>
    /// Rents a destination buffer from <see cref="ArrayPool{T}.Shared"/> and copies the cropped
    /// BGRA rows into the first <paramref name="dstBytes"/> bytes.
    /// </summary>
    internal static byte[] RentCropBgraBuffer(
        ReadOnlySpan<byte> src,
        int srcWidth,
        int srcHeight,
        int srcStride,
        int cropX,
        int cropY,
        int cropW,
        int cropH,
        out int dstBytes)
    {
        dstBytes = ValidateAndComputeCropBytes(srcWidth, srcHeight, cropX, cropY, cropW, cropH);
        byte[] dst = ArrayPool<byte>.Shared.Rent(dstBytes);
        try
        {
            CropBgraBufferInto(src, srcStride, cropX, cropY, cropW, cropH, dst.AsSpan(0, dstBytes));
            return dst;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(dst);
            throw;
        }
    }

    /// <summary>True when a buffer looks like a failed PrintWindow (nearly all black).</summary>
    internal static bool LooksBlankBgra(ReadOnlySpan<byte> bgra, int sampleStride)
    {
        if (bgra.Length < 16)
        {
            return true;
        }

        int step = Math.Max(4, sampleStride);
        int nonBlack = 0;
        int samples = 0;
        for (int i = 0; i + 3 < bgra.Length; i += step)
        {
            samples++;
            byte b = bgra[i];
            byte g = bgra[i + 1];
            byte r = bgra[i + 2];
            if (b > 8 || g > 8 || r > 8)
            {
                nonBlack++;
                if (nonBlack >= 3)
                {
                    return false;
                }
            }
        }

        return samples == 0 || nonBlack == 0;
    }

    private static int ValidateAndComputeCropBytes(
        int srcWidth,
        int srcHeight,
        int cropX,
        int cropY,
        int cropW,
        int cropH)
    {
        if (cropX < 0 || cropY < 0 || cropW <= 0 || cropH <= 0
            || cropX + cropW > srcWidth || cropY + cropH > srcHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(cropX), "Crop rect outside source.");
        }

        return CaptureBuffers.BgraByteCount(cropW, cropH);
    }

    private static void CropBgraBufferInto(
        ReadOnlySpan<byte> src,
        int srcStride,
        int cropX,
        int cropY,
        int cropW,
        int cropH,
        Span<byte> dst)
    {
        int dstStride = CaptureBuffers.BgraStride(cropW);
        int rowBytes = cropW * 4;
        for (int y = 0; y < cropH; y++)
        {
            var row = src.Slice(((cropY + y) * srcStride) + (cropX * 4), rowBytes);
            row.CopyTo(dst.Slice(y * dstStride, rowBytes));
        }
    }

    private static bool TryCaptureViaWinRt(
        IntPtr windowHwnd,
        int cropX,
        int cropY,
        int width,
        int height,
        out CapturedFrame frame)
    {
        frame = null;
        if (!WinRtGraphicsCapture.TryCaptureWindow(windowHwnd, captureCursor: false, out CapturedFrame full)
            || full == null)
        {
            return false;
        }

        byte[] crop = null;
        try
        {
            if (full.Width < cropX + width || full.Height < cropY + height)
            {
                Log.Info(
                    $"Snip scroll WinRT window frame too small {full.Width}x{full.Height} need crop ({cropX},{cropY},{width}x{height})",
                    typeof(ScrollWindowCapture));
                return false;
            }

            using ScrollTilePixels owned = full.ToOwnedPixels();
            full = null; // disposed by ToOwnedPixels
            int dstStride = CaptureBuffers.BgraStride(width);
            crop = RentCropBgraBuffer(
                owned.Pixels,
                owned.Width,
                owned.Height,
                owned.Stride,
                cropX,
                cropY,
                width,
                height,
                out int dstBytes);
            if (LooksBlankBgra(crop.AsSpan(0, dstBytes), dstStride))
            {
                Log.Info("Snip scroll WinRT window crop looked blank — reject", typeof(ScrollWindowCapture));
                return false;
            }

            frame = new CapturedFrame(crop, width, height, dstStride, rentedFromPool: true);
            crop = null; // ownership transferred to CapturedFrame
            Log.Info(
                $"Snip scroll tile via WinRT CreateForWindow crop ({cropX},{cropY},{width}x{height})",
                typeof(ScrollWindowCapture));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll WinRT window crop failed: {ex.Message}", typeof(ScrollWindowCapture));
            frame?.Dispose();
            frame = null;
            return false;
        }
        finally
        {
            if (crop != null)
            {
                ArrayPool<byte>.Shared.Return(crop);
            }

            full?.Dispose();
        }
    }

    private static bool TryCaptureViaPrintWindow(
        IntPtr windowHwnd,
        int winW,
        int winH,
        int cropX,
        int cropY,
        int width,
        int height,
        out CapturedFrame frame)
    {
        frame = null;
        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        IntPtr memDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr old = IntPtr.Zero;
        byte[] crop = null;
        try
        {
            memDc = Win32.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                return false;
            }

            var bmi = CaptureBuffers.CreateTopDownBgraInfo(winW, winH);
            dib = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return false;
            }

            old = Win32.SelectObject(memDc, dib);
            bool printed = Win32.PrintWindow(windowHwnd, memDc, Win32.PwRenderFullContent);
            if (!printed)
            {
                printed = Win32.PrintWindow(windowHwnd, memDc, 0);
            }

            if (!printed)
            {
                Log.Warn(
                    $"Snip scroll PrintWindow failed err={Marshal.GetLastWin32Error()}",
                    typeof(ScrollWindowCapture));
                return false;
            }

            if (old != IntPtr.Zero)
            {
                _ = Win32.SelectObject(memDc, old);
                old = IntPtr.Zero;
            }

            int srcStride = CaptureBuffers.BgraStride(winW);
            int srcBytes = CaptureBuffers.BgraByteCount(winW, winH);
            int dstStride = CaptureBuffers.BgraStride(width);
            unsafe
            {
                var srcSpan = new ReadOnlySpan<byte>((void*)bits, srcBytes);
                if (LooksBlankBgra(srcSpan, srcStride))
                {
                    Log.Info("Snip scroll PrintWindow looked blank — reject", typeof(ScrollWindowCapture));
                    return false;
                }

                crop = RentCropBgraBuffer(
                    srcSpan,
                    winW,
                    winH,
                    srcStride,
                    cropX,
                    cropY,
                    width,
                    height,
                    out int dstBytes);
                if (LooksBlankBgra(crop.AsSpan(0, dstBytes), dstStride))
                {
                    Log.Info("Snip scroll PrintWindow crop looked blank — reject", typeof(ScrollWindowCapture));
                    return false;
                }
            }

            frame = new CapturedFrame(crop, width, height, dstStride, rentedFromPool: true);
            crop = null; // ownership transferred to CapturedFrame
            Log.Info(
                $"Snip scroll tile via PrintWindow crop ({cropX},{cropY},{width}x{height})",
                typeof(ScrollWindowCapture));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll PrintWindow capture failed: {ex.Message}", typeof(ScrollWindowCapture));
            frame?.Dispose();
            frame = null;
            return false;
        }
        finally
        {
            if (crop != null)
            {
                ArrayPool<byte>.Shared.Return(crop);
            }

            if (old != IntPtr.Zero && memDc != IntPtr.Zero)
            {
                _ = Win32.SelectObject(memDc, old);
            }

            if (dib != IntPtr.Zero)
            {
                _ = Win32.DeleteObject(dib);
            }

            if (memDc != IntPtr.Zero)
            {
                _ = Win32.DeleteDC(memDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                _ = Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
