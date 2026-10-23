// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Mixed-DPI helpers: map overlay DIP selections to virtual-desktop physical pixels
/// (and the reverse for floating UI) without assuming a single scale across monitors.
/// </summary>
internal static class DpiCoordinateMapping
{
    /// <summary>
    /// Maps a DIP rect in overlay space to physical pixels covering
    /// <paramref name="physicalBounds"/> proportionally (robust when
    /// <c>logical*dpi</c> rounding disagrees with the native client size).
    /// </summary>
    public static Int32Rect DipSelectionToPhysical(
        Rect selectionDip,
        Size logicalSize,
        Int32Rect physicalBounds)
    {
        if (logicalSize.Width <= 0 || logicalSize.Height <= 0)
        {
            return new Int32Rect(
                physicalBounds.X,
                physicalBounds.Y,
                Math.Max(1, physicalBounds.Width),
                Math.Max(1, physicalBounds.Height));
        }

        int x = physicalBounds.X + (int)Math.Round(selectionDip.X / logicalSize.Width * physicalBounds.Width);
        int y = physicalBounds.Y + (int)Math.Round(selectionDip.Y / logicalSize.Height * physicalBounds.Height);
        int w = Math.Max(1, (int)Math.Round(selectionDip.Width / logicalSize.Width * physicalBounds.Width));
        int h = Math.Max(1, (int)Math.Round(selectionDip.Height / logicalSize.Height * physicalBounds.Height));
        return new Int32Rect(x, y, w, h);
    }

    /// <summary>
    /// Maps a DIP selection to client-pixel hole rect using the live client size.
    /// </summary>
    public static Int32Rect DipSelectionToClientHole(
        Rect selectionDip,
        Size logicalSize,
        int clientWidth,
        int clientHeight)
    {
        if (logicalSize.Width <= 0 || logicalSize.Height <= 0)
        {
            return new Int32Rect(0, 0, Math.Max(1, clientWidth), Math.Max(1, clientHeight));
        }

        int x = (int)Math.Round(selectionDip.X / logicalSize.Width * clientWidth);
        int y = (int)Math.Round(selectionDip.Y / logicalSize.Height * clientHeight);
        int w = Math.Max(1, (int)Math.Round(selectionDip.Width / logicalSize.Width * clientWidth));
        int h = Math.Max(1, (int)Math.Round(selectionDip.Height / logicalSize.Height * clientHeight));
        return new Int32Rect(x, y, w, h);
    }

    /// <summary>
    /// Effective DPI scale of the monitor that contains the physical point (fallback <paramref name="fallbackScale"/>).
    /// </summary>
    public static double EffectiveDpiScaleAtPhysical(int physicalX, int physicalY, double fallbackScale)
    {
        try
        {
            var pt = new Win32.POINT { X = physicalX, Y = physicalY };
            IntPtr monitor = Win32.MonitorFromPoint(pt, Win32.MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero
                && Win32.GetDpiForMonitor(monitor, Win32.MdtEffectiveDpi, out uint dpiX, out _) == 0
                && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (Exception)
        {
            // Keep fallback.
        }

        return fallbackScale > 0.01 ? fallbackScale : 1.0;
    }
}
