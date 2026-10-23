// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Enumerates visible top-level desktop windows (via <c>EnumWindows</c> + DWM extended frame bounds)
/// and queries UI Automation sub-elements for hover auto-snap in <see cref="UI.SnipWindow"/>.
/// </summary>
internal static class WindowSnapService
{
    internal readonly record struct SnapCandidate(IntPtr Hwnd, Int32Rect ScreenBounds);

    /// <summary>
    /// Captures visible, uncloaked, non-minimized top-level windows in Z-order (front to back).
    /// </summary>
    public static IReadOnlyList<SnapCandidate> CaptureTopLevelWindows(IntPtr excludeHwnd)
    {
        var list = new List<SnapCandidate>(32);
        var classBuf = new StringBuilder(64);
        uint currentPid = (uint)Environment.ProcessId;

        Win32.EnumWindows(
            (hwnd, _) =>
            {
                if (hwnd == IntPtr.Zero || hwnd == excludeHwnd)
                {
                    return true;
                }

                if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd))
                {
                    return true;
                }

                if (Win32.DwmGetWindowAttribute(hwnd, Win32.DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                {
                    return true;
                }

                classBuf.Clear();
                if (Win32.GetClassName(hwnd, classBuf, classBuf.Capacity) > 0)
                {
                    string cls = classBuf.ToString();
                    if (string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                if (excludeHwnd == IntPtr.Zero)
                {
                    uint tid = Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                    if (tid != 0 && pid == currentPid)
                    {
                        long exStyle = Win32.GetWindowLongPtr(hwnd, Win32.GwlExStyle).ToInt64();
                        if ((exStyle & Win32.WsExTransparent) != 0)
                        {
                            return true;
                        }
                    }
                }

                if (!TryGetWindowFrameBounds(hwnd, out Int32Rect bounds))
                {
                    return true;
                }

                if (bounds.Width >= 16 && bounds.Height >= 16)
                {
                    list.Add(new SnapCandidate(hwnd, bounds));
                }

                return true;
            },
            IntPtr.Zero);

        return list;
    }

    /// <summary>
    /// Returns the front-most window candidate whose <see cref="SnapCandidate.ScreenBounds"/>
    /// contains physical screen coordinates (<paramref name="screenX"/>, <paramref name="screenY"/>).
    /// </summary>
    public static SnapCandidate? FindHoveredWindow(IReadOnlyList<SnapCandidate> candidates, int screenX, int screenY)
    {
        if (candidates == null)
        {
            return null;
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (ContainsPoint(c.ScreenBounds, screenX, screenY))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>
    /// Queries UI Automation (<see cref="AutomationElement"/>) or Win32 child window bounds
    /// for the sub-control at (<paramref name="screenX"/>, <paramref name="screenY"/>).
    /// </summary>
    public static bool TryFindSubElementBounds(
        IReadOnlyList<SnapCandidate> candidates,
        IntPtr overlayHwnd,
        int screenX,
        int screenY,
        out Int32Rect subBounds)
    {
        subBounds = default;
        var hoveredWindow = FindHoveredWindow(candidates, screenX, screenY);
        if (hoveredWindow == null)
        {
            return false;
        }

        IntPtr targetHwnd = DrillDownChildWindow(hoveredWindow.Value.Hwnd, screenX, screenY);

        if (TryQueryUiaFromHwnd(targetHwnd, screenX, screenY, overlayHwnd, out Int32Rect uiaRect)
            && uiaRect.Width >= 6
            && uiaRect.Height >= 6)
        {
            subBounds = uiaRect;
            return true;
        }

        if (targetHwnd != IntPtr.Zero
            && targetHwnd != hoveredWindow.Value.Hwnd
            && Win32.GetWindowRect(targetHwnd, out Win32.RECT childRc)
            && childRc.Width >= 6
            && childRc.Height >= 6)
        {
            subBounds = new Int32Rect(childRc.Left, childRc.Top, childRc.Width, childRc.Height);
            return true;
        }

        subBounds = hoveredWindow.Value.ScreenBounds;
        return subBounds.Width >= 6 && subBounds.Height >= 6;
    }

    /// <summary>
    /// Clips a physical screen rectangle to <paramref name="captureBounds"/> and maps it to
    /// overlay DIP coordinates.
    /// </summary>
    public static Rect? ScreenRectToOverlayDip(Int32Rect screenRect, Int32Rect captureBounds, Size overlayDipSize)
    {
        if (captureBounds.Width <= 0 || captureBounds.Height <= 0
            || overlayDipSize.Width <= 0 || overlayDipSize.Height <= 0)
        {
            return null;
        }

        int screenRight = screenRect.X + screenRect.Width;
        int screenBottom = screenRect.Y + screenRect.Height;
        int capRight = captureBounds.X + captureBounds.Width;
        int capBottom = captureBounds.Y + captureBounds.Height;

        int left = Math.Max(screenRect.X, captureBounds.X);
        int top = Math.Max(screenRect.Y, captureBounds.Y);
        int right = Math.Min(screenRight, capRight);
        int bottom = Math.Min(screenBottom, capBottom);

        int w = right - left;
        int h = bottom - top;
        if (w < 5 || h < 5)
        {
            return null;
        }

        double scaleX = overlayDipSize.Width / captureBounds.Width;
        double scaleY = overlayDipSize.Height / captureBounds.Height;

        double dipX = (left - captureBounds.X) * scaleX;
        double dipY = (top - captureBounds.Y) * scaleY;
        double dipW = w * scaleX;
        double dipH = h * scaleY;

        return new Rect(dipX, dipY, dipW, dipH);
    }

    public static bool ContainsPoint(Int32Rect rect, int x, int y)
        => x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;

    private static bool TryGetWindowFrameBounds(IntPtr hwnd, out Int32Rect bounds)
    {
        if (Win32.DwmGetWindowAttribute(
                hwnd,
                Win32.DwmwaExtendedFrameBounds,
                out Win32.RECT dwmRect,
                Marshal.SizeOf<Win32.RECT>()) == 0
            && dwmRect.Width > 0
            && dwmRect.Height > 0)
        {
            bounds = new Int32Rect(dwmRect.Left, dwmRect.Top, dwmRect.Width, dwmRect.Height);
            return true;
        }

        if (Win32.GetWindowRect(hwnd, out Win32.RECT winRect)
            && winRect.Width > 0
            && winRect.Height > 0)
        {
            bounds = new Int32Rect(winRect.Left, winRect.Top, winRect.Width, winRect.Height);
            return true;
        }

        bounds = default;
        return false;
    }

    private static IntPtr DrillDownChildWindow(IntPtr rootHwnd, int screenX, int screenY)
    {
        IntPtr current = rootHwnd;
        for (int depth = 0; depth < 16 && current != IntPtr.Zero; depth++)
        {
            var clientPt = new Win32.POINT { X = screenX, Y = screenY };
            if (!Win32.ScreenToClient(current, ref clientPt))
            {
                break;
            }

            IntPtr child = Win32.RealChildWindowFromPoint(current, clientPt);
            if (child == IntPtr.Zero || child == current || !Win32.IsWindowVisible(child))
            {
                break;
            }

            current = child;
        }

        return current;
    }

    private static bool TryQueryUiaFromHwnd(
        IntPtr targetHwnd,
        int screenX,
        int screenY,
        IntPtr overlayHwnd,
        out Int32Rect result)
    {
        result = default;
        try
        {
            IntPtr prevStyle = IntPtr.Zero;
            bool toggledStyle = false;
            if (overlayHwnd != IntPtr.Zero)
            {
                prevStyle = Win32.GetWindowLongPtr(overlayHwnd, Win32.GwlExStyle);
                long nextStyle = prevStyle.ToInt64() | Win32.WsExTransparent;
                Win32.SetWindowLongPtr(overlayHwnd, Win32.GwlExStyle, new IntPtr(nextStyle));
                toggledStyle = true;
            }

            AutomationElement el;
            try
            {
                el = AutomationElement.FromPoint(new Point(screenX, screenY));
            }
            finally
            {
                if (toggledStyle)
                {
                    Win32.SetWindowLongPtr(overlayHwnd, Win32.GwlExStyle, prevStyle);
                }
            }

            if (el == null && targetHwnd != IntPtr.Zero)
            {
                el = AutomationElement.FromHandle(targetHwnd);
            }

            if (el == null)
            {
                return false;
            }

            Rect r = el.Current.BoundingRectangle;
            if (r.IsEmpty || double.IsInfinity(r.Width) || double.IsInfinity(r.Height) || r.Width < 4 || r.Height < 4)
            {
                return false;
            }

            result = new Int32Rect(
                (int)Math.Round(r.Left),
                (int)Math.Round(r.Top),
                (int)Math.Round(r.Width),
                (int)Math.Round(r.Height));
            return ContainsPoint(result, screenX, screenY);
        }
        catch
        {
            return false;
        }
    }
}
