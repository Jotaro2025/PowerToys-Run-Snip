// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Adapted from LdotJdot/TDS (MIT) Win32WindowRegion for Snip overlay hole-punch.
using System;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Win32 window region helpers: punch a rectangular hole in the overlay so the
/// selection shows live desktop content for scroll capture.
/// Coordinates are physical client pixels (selection × dpiScale).
/// </summary>
public static class Win32WindowRegion
{
    /// <summary>
    /// Shrink the HWND to everything except <paramref name="holeX"/>… (client coords).
    /// Returns <c>false</c> when no region was applied (invalid args, empty hole, or
    /// hole would cover the entire client and leave a zero-area window — that blanked
    /// the overlay as a white/empty flash).
    /// </summary>
    public static bool ApplySelectionHole(
        IntPtr hwnd,
        int clientWidth,
        int clientHeight,
        int holeX,
        int holeY,
        int holeWidth,
        int holeHeight)
    {
        if (hwnd == IntPtr.Zero || clientWidth <= 0 || clientHeight <= 0)
        {
            return false;
        }

        if (!TryClampHole(clientWidth, clientHeight, holeX, holeY, holeWidth, holeHeight,
                out int left, out int top, out int right, out int bottom))
        {
            return false;
        }

        // Full-client hole → CombineRgn DIFF yields an empty region → window vanishes (白屏).
        if (left == 0 && top == 0 && right == clientWidth && bottom == clientHeight)
        {
            return false;
        }

        IntPtr full = Win32.CreateRectRgn(0, 0, clientWidth, clientHeight);
        IntPtr hole = Win32.CreateRectRgn(left, top, right, bottom);
        IntPtr result = Win32.CreateRectRgn(0, 0, 0, 0);
        _ = Win32.CombineRgn(result, full, hole, Win32.RgnDiff);
        _ = Win32.DeleteObject(full);
        _ = Win32.DeleteObject(hole);
        _ = Win32.SetWindowRgn(hwnd, result, true);
        return true;
    }

    /// <summary>
    /// Clamp hole to the client rect. Returns false when the clamped hole has no area.
    /// </summary>
    internal static bool TryClampHole(
        int clientWidth,
        int clientHeight,
        int holeX,
        int holeY,
        int holeWidth,
        int holeHeight,
        out int left,
        out int top,
        out int right,
        out int bottom)
    {
        left = Math.Clamp(holeX, 0, clientWidth);
        top = Math.Clamp(holeY, 0, clientHeight);
        right = Math.Clamp(holeX + holeWidth, 0, clientWidth);
        bottom = Math.Clamp(holeY + holeHeight, 0, clientHeight);
        return right > left && bottom > top;
    }

    /// <summary>Restore the HWND to its normal rectangular shape.</summary>
    public static void Clear(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = Win32.SetWindowRgn(hwnd, IntPtr.Zero, true);
    }

    /// <summary>Client area size in physical pixels, or false if unavailable.</summary>
    public static bool TryGetClientSize(IntPtr hwnd, out int width, out int height)
    {
        width = height = 0;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!Win32.GetClientRect(hwnd, out var rc))
        {
            return false;
        }

        width = rc.Width;
        height = rc.Height;
        return width > 0 && height > 0;
    }
}
