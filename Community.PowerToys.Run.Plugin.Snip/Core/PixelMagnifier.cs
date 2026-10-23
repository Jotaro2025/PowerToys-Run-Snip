// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Zero-allocation pixel sampler, 11×11 zoomed grid renderer, and HUD placement math
/// for the snip overlay magnifier and color picker.
/// </summary>
internal static class PixelMagnifier
{
    public const int GridSize = 11;

    public const int ZoomFactor = 10;

    public const int OutputPixelSize = GridSize * ZoomFactor;

    /// <summary>
    /// Samples the clamped BGRA32 pixel at (<paramref name="px"/>, <paramref name="py"/>) directly from an unmanaged back buffer.
    /// </summary>
    public static unsafe void SamplePixelBgra(
        byte* src,
        int srcStride,
        int srcWidth,
        int srcHeight,
        int px,
        int py,
        out byte r,
        out byte g,
        out byte b)
    {
        if (src == null || srcWidth <= 0 || srcHeight <= 0 || srcStride < srcWidth * 4)
        {
            r = g = b = 0;
            return;
        }

        int cx = Math.Clamp(px, 0, srcWidth - 1);
        int cy = Math.Clamp(py, 0, srcHeight - 1);
        byte* p = src + (cy * srcStride) + (cx * 4);
        b = p[0];
        g = p[1];
        r = p[2];
    }

    /// <summary>Managed span overload of <see cref="SamplePixelBgra(byte*,int,int,int,int,int,out byte,out byte,out byte)"/>.</summary>
    public static unsafe void SamplePixelBgra(
        ReadOnlySpan<byte> src,
        int srcStride,
        int srcWidth,
        int srcHeight,
        int px,
        int py,
        out byte r,
        out byte g,
        out byte b)
    {
        if (src.IsEmpty || srcWidth <= 0 || srcHeight <= 0 || srcStride < srcWidth * 4 || src.Length < srcStride * srcHeight)
        {
            r = g = b = 0;
            return;
        }

        fixed (byte* sp = &MemoryMarshal.GetReference(src))
        {
            SamplePixelBgra(sp, srcStride, srcWidth, srcHeight, px, py, out r, out g, out b);
        }
    }

    /// <summary>
    /// Fills a zoomed BGRA32 preview buffer (<paramref name="gridSize"/> × <paramref name="zoomFactor"/> square)
    /// centered on (<paramref name="centerX"/>, <paramref name="centerY"/>) with a center crosshair highlight.
    /// </summary>
    public static unsafe void FillZoomedGrid(
        byte* src,
        int srcStride,
        int srcWidth,
        int srcHeight,
        int centerX,
        int centerY,
        byte* dest,
        int destStride,
        int gridSize = GridSize,
        int zoomFactor = ZoomFactor)
    {
        if (src == null || dest == null || srcWidth <= 0 || srcHeight <= 0 || gridSize <= 0 || zoomFactor <= 0)
        {
            return;
        }

        int outSize = gridSize * zoomFactor;
        if (srcStride < srcWidth * 4 || destStride < outSize * 4)
        {
            return;
        }

        int half = gridSize / 2;
        for (int gy = 0; gy < gridSize; gy++)
        {
            int sy = Math.Clamp(centerY + gy - half, 0, srcHeight - 1);
            byte* sRow = src + (sy * srcStride);
            for (int gx = 0; gx < gridSize; gx++)
            {
                int sx = Math.Clamp(centerX + gx - half, 0, srcWidth - 1);
                byte* sp = sRow + (sx * 4);
                uint packed = (uint)(sp[0] | (sp[1] << 8) | (sp[2] << 16) | (255u << 24));

                int dy0 = gy * zoomFactor;
                int dx0 = gx * zoomFactor;
                for (int zy = 0; zy < zoomFactor; zy++)
                {
                    var dRow = new Span<uint>(dest + ((dy0 + zy) * destStride) + (dx0 * 4), zoomFactor);
                    dRow.Fill(packed);
                }
            }
        }

        // Draw crisp 1px crosshair outline around the center cell (half, half).
        int c0 = half * zoomFactor;
        int c1 = c0 + zoomFactor - 1;
        const uint accentPacked = 0xFF6ED248u; // ARGB(255, 72, 210, 110) in little-endian BGRA
        for (int i = c0; i <= c1; i++)
        {
            *(uint*)(dest + (c0 * destStride) + (i * 4)) = accentPacked;
            *(uint*)(dest + (c1 * destStride) + (i * 4)) = accentPacked;
            *(uint*)(dest + (i * destStride) + (c0 * 4)) = accentPacked;
            *(uint*)(dest + (i * destStride) + (c1 * 4)) = accentPacked;
        }
    }

    /// <summary>Managed span overload of <see cref="FillZoomedGrid(byte*,int,int,int,int,int,byte*,int,int,int)"/>.</summary>
    public static unsafe void FillZoomedGrid(
        ReadOnlySpan<byte> src,
        int srcStride,
        int srcWidth,
        int srcHeight,
        int centerX,
        int centerY,
        Span<byte> dest,
        int destStride,
        int gridSize = GridSize,
        int zoomFactor = ZoomFactor)
    {
        int outSize = gridSize * zoomFactor;
        if (src.IsEmpty || dest.IsEmpty || src.Length < srcStride * srcHeight || dest.Length < destStride * outSize)
        {
            return;
        }

        fixed (byte* sp = &MemoryMarshal.GetReference(src))
        {
            fixed (byte* dp = &MemoryMarshal.GetReference(dest))
            {
                FillZoomedGrid(sp, srcStride, srcWidth, srcHeight, centerX, centerY, dp, destStride, gridSize, zoomFactor);
            }
        }
    }

    /// <summary>Formats RGB bytes as <c>#RRGGBB</c>.</summary>
    public static string FormatHex(byte r, byte g, byte b)
        => string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

    /// <summary>Formats RGB bytes as <c>RGB(r, g, b)</c>.</summary>
    public static string FormatRgb(byte r, byte g, byte b)
        => string.Create(CultureInfo.InvariantCulture, $"RGB({r}, {g}, {b})");

    /// <summary>
    /// Computes the top-left DIP position for the magnifier HUD near <paramref name="cursorDip"/>,
    /// flipping left/up when approaching the right/bottom edges of <paramref name="monitorDip"/>.
    /// </summary>
    public static Point ComputeHudPosition(Point cursorDip, Size hudSize, Rect monitorDip)
    {
        const double offset = 20.0;
        const double margin = 8.0;

        double x = cursorDip.X + offset;
        double y = cursorDip.Y + offset;

        if (x + hudSize.Width + margin > monitorDip.Right)
        {
            x = cursorDip.X - hudSize.Width - offset;
        }

        if (y + hudSize.Height + margin > monitorDip.Bottom)
        {
            y = cursorDip.Y - hudSize.Height - offset;
        }

        x = Math.Clamp(x, monitorDip.Left + margin, Math.Max(monitorDip.Left + margin, monitorDip.Right - hudSize.Width - margin));
        y = Math.Clamp(y, monitorDip.Top + margin, Math.Max(monitorDip.Top + margin, monitorDip.Bottom - hudSize.Height - margin));
        return new Point(x, y);
    }
}
