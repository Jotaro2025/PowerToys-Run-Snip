// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Sizing and BITMAPINFO helpers for a single managed BGRA32 capture buffer.
/// Used by <see cref="Win32CaptureService"/> so CreateDIBSection writes pixels
/// without an AllocHGlobal + Marshal.Copy staging buffer.
/// </summary>
internal static class CaptureBuffers
{
    /// <summary>BGRA32 bytes per row (no padding — width is used as-is for 32bpp).</summary>
    public static int BgraStride(int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        return checked(width * 4);
    }

    /// <summary>Total bytes for a top-down BGRA32 frame.</summary>
    public static int BgraByteCount(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return checked(BgraStride(width) * height);
    }

    /// <summary>
    /// BITMAPINFO for a top-down (negative height) 32bpp BI_RGB DIB — the layout
    /// WPF <c>Bgra32</c> and <see cref="CapturedFrame"/> expect.
    /// </summary>
    public static Win32.BITMAPINFO CreateTopDownBgraInfo(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        return new Win32.BITMAPINFO
        {
            BmiHeader = new Win32.BITMAPINFOHEADER
            {
                BiSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                BiWidth = width,
                BiHeight = -height,
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = Win32.BiRgb,
            },
        };
    }
}
