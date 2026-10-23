// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Windows capture implementation using GDI BitBlt. No System.Drawing, so the
/// plugin stays trim and AOT friendly.
/// </summary>
public sealed class Win32CaptureService : ICaptureService
{
    public IReadOnlyList<ScreenInfo> GetScreens()
    {
        var list = new List<ScreenInfo>();
        GetScreensInto(list);
        if (list.Count == 0)
        {
            return list;
        }

        // Build a synthetic "virtual desktop" entry that covers the union of all monitor bounds.
        int left = list.Min(s => (int)s.Bounds.X);
        int top = list.Min(s => (int)s.Bounds.Y);
        int right = list.Max(s => (int)s.Bounds.Right);
        int bottom = list.Max(s => (int)s.Bounds.Bottom);
        var primaryScale = list.FirstOrDefault(s => s.IsPrimary)?.DpiScale ?? list[0].DpiScale;
        list.Add(new ScreenInfo(
            Handle: IntPtr.Zero,
            DeviceName: VirtualDesktopName,
            Bounds: new Rect(left, top, right - left, bottom - top),
            WorkingArea: new Rect(left, top, right - left, bottom - top),
            DpiScale: primaryScale,
            IsPrimary: true));
        return list;
    }

    private static void GetScreensInto(List<ScreenInfo> list)
    {
        Win32.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr _, ref Win32.RECT _, IntPtr _) =>
        {
            var info = new Win32.MONITORINFOEX { CbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (!Win32.GetMonitorInfo(hMonitor, ref info))
            {
                return true;
            }

            var bounds = new Rect(info.RcMonitor.Left, info.RcMonitor.Top, info.RcMonitor.Width, info.RcMonitor.Height);
            var work = new Rect(info.RcWork.Left, info.RcWork.Top, info.RcWork.Width, info.RcWork.Height);
            list.Add(new ScreenInfo(
                Handle: hMonitor,
                DeviceName: info.SzDevice,
                Bounds: bounds,
                WorkingArea: work,
                DpiScale: GetMonitorDpiScale(hMonitor),
                IsPrimary: (info.DwFlags & 1) != 0));
            return true;
        };

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
    }

    private static double GetMonitorDpiScale(IntPtr hMonitor)
    {
        if (Win32.GetDpiForMonitor(hMonitor, Win32.MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return 1.0;
    }

    /// <summary>Device name of the synthetic entry covering the union of all monitors.</summary>
    public const string VirtualDesktopName = "VirtualDesktop";

    /// <summary>
    /// Capture target: the single physical monitor when only one exists (unchanged behavior),
    /// otherwise the synthetic virtual-desktop entry so a selection can span monitors.
    /// </summary>
    public static ScreenInfo ChooseCaptureTarget(IReadOnlyList<ScreenInfo> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        var physical = PhysicalScreens(screens);
        if (physical.Count > 1)
        {
            var virtualDesktop = screens.FirstOrDefault(s => s.DeviceName == VirtualDesktopName);
            if (virtualDesktop != null)
            {
                return virtualDesktop;
            }
        }

        return GetScreenAtCursor(screens);
    }

    /// <summary>All real monitors (without the synthetic virtual-desktop entry).</summary>
    public static IReadOnlyList<ScreenInfo> PhysicalScreens(IReadOnlyList<ScreenInfo> screens)
        => screens?.Where(s => s.DeviceName != VirtualDesktopName).ToList() ?? [];

    /// <summary>Returns the monitor under the cursor, or primary / first physical screen.</summary>
    public static ScreenInfo GetScreenAtCursor(IReadOnlyList<ScreenInfo> screens)
    {
        var physical = screens.Where(s => s.DeviceName != VirtualDesktopName).ToList();
        if (physical.Count == 0)
        {
            return screens.First(s => s.IsPrimary);
        }

        if (Win32.GetCursorPos(out var pt))
        {
            foreach (var s in physical)
            {
                if (s.Bounds.Contains(new Point(pt.X, pt.Y)))
                {
                    return s;
                }
            }
        }

        return physical.FirstOrDefault(s => s.IsPrimary) ?? physical[0];
    }

    /// <summary>
    /// Physical-pixel bounds of the foreground top-level window, or <c>false</c> when
    /// there is no visible non-minimized target (e.g. desktop / empty).
    /// </summary>
    public static bool TryGetForegroundWindowBounds(out Int32Rect bounds)
    {
        bounds = default;
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || Win32.IsIconic(hwnd) || !Win32.IsWindowVisible(hwnd))
        {
            return false;
        }

        if (!Win32.GetWindowRect(hwnd, out Win32.RECT rc))
        {
            return false;
        }

        int w = rc.Width;
        int h = rc.Height;
        if (w < 5 || h < 5)
        {
            return false;
        }

        bounds = new Int32Rect(rc.Left, rc.Top, w, h);
        return true;
    }

    /// <summary>Monitor whose bounds contain <paramref name="physicalPoint"/>, else cursor/primary.</summary>
    public static ScreenInfo GetScreenAtPhysicalPoint(IReadOnlyList<ScreenInfo> screens, Point physicalPoint)
    {
        var physical = PhysicalScreens(screens);
        foreach (var s in physical)
        {
            if (s.Bounds.Contains(physicalPoint))
            {
                return s;
            }
        }

        return GetScreenAtCursor(screens);
    }

    public WriteableBitmap CaptureScreen(ScreenInfo screen)
    {
        using var frame = CaptureScreenPixels(screen);
        return frame.ToWriteableBitmap();
    }

    /// <summary>
    /// BitBlt into a top-down BGRA <c>CreateDIBSection</c> and return a
    /// <see cref="CapturedFrame"/> that <strong>owns</strong> the HBITMAP.
    /// Safe off the UI thread — no <see cref="WriteableBitmap"/> here and no
    /// full-frame managed <c>byte[]</c>. UI STA calls
    /// <see cref="CapturedFrame.ToWriteableBitmap"/> which Span-copies into
    /// BackBuffer then <c>DeleteObject</c>. Avoids the prior DIB→byte[]→WB
    /// double peak. Bounds are physical pixels (per-monitor DPI).
    /// </summary>
    public CapturedFrame CaptureScreenPixels(ScreenInfo screen)
    {
        int w = (int)screen.Bounds.Width;
        int h = (int)screen.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            throw new ArgumentException("Screen has zero area", nameof(screen));
        }

        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC(NULL) failed.");
        }

        IntPtr memDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr old = IntPtr.Zero;
        try
        {
            memDc = Win32.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleDC failed.");
            }

            // Top-down 32bpp DIB: BitBlt writes final pixels here.
            var bmi = CaptureBuffers.CreateTopDownBgraInfo(w, h);
            dib = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDIBSection failed.");
            }

            old = Win32.SelectObject(memDc, dib);
            if (!Win32.BitBlt(
                    memDc,
                    0,
                    0,
                    w,
                    h,
                    screenDc,
                    (int)screen.Bounds.X,
                    (int)screen.Bounds.Y,
                    Win32.SrcCopy | (int)Win32.CaptureBlt))
            {
                throw new InvalidOperationException("BitBlt failed.");
            }

            // Unselect before tearing down DCs; DIB stays alive until CapturedFrame.Dispose.
            if (old != IntPtr.Zero)
            {
                _ = Win32.SelectObject(memDc, old);
                old = IntPtr.Zero;
            }

            int stride = CaptureBuffers.BgraStride(w);
            var frame = new CapturedFrame(dib, bits, w, h, stride);
            dib = IntPtr.Zero; // ownership transferred — finally must not DeleteObject
            return frame;
        }
        finally
        {
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

    /// <summary>
    /// Deliver <c>WM_MOUSEWHEEL</c> to the scroll target under a screen point,
    /// skipping the overlay HWND. Cursor position is irrelevant.
    /// </summary>
    public static bool TrySendWheelAt(
        int physicalX,
        int physicalY,
        int delta,
        IntPtr skipOverlayHwnd = default)
    {
        if (delta == 0)
        {
            return false;
        }

        var pt = new Win32.POINT { X = physicalX, Y = physicalY };
        IntPtr target = ResolveScrollTargetAt(pt, skipOverlayHwnd);
        if (target == IntPtr.Zero)
        {
            return false;
        }

#pragma warning disable CA2020 // WM_MOUSEWHEEL packs signed delta / coords into WPARAM/LPARAM bits.
        IntPtr wParam = unchecked((IntPtr)((delta << 16) & 0xFFFFFFFF));
        IntPtr lParam = unchecked((IntPtr)((uint)(((physicalY & 0xFFFF) << 16) | (physicalX & 0xFFFF))));
#pragma warning restore CA2020
        _ = Win32.SendMessage(target, Win32.WmMouseWheel, wParam, lParam);
        return true;
    }

    private static IntPtr ResolveScrollTargetAt(Win32.POINT pt, IntPtr skipOverlayHwnd)
    {
        IntPtr hit = Win32.WindowFromPoint(pt);
        if (hit == IntPtr.Zero || hit == skipOverlayHwnd)
        {
            return IntPtr.Zero;
        }

        IntPtr root = Win32.GetAncestor(hit, Win32.GaRoot);
        if (root == IntPtr.Zero || root == skipOverlayHwnd)
        {
            return IntPtr.Zero;
        }

        return FindDeepestChildAt(root, pt);
    }

    private static IntPtr FindDeepestChildAt(IntPtr root, Win32.POINT screenPt)
    {
        IntPtr current = root;
        for (int i = 0; i < 32; i++)
        {
            var clientPt = screenPt;
            _ = Win32.ScreenToClient(current, ref clientPt);
            IntPtr child = Win32.ChildWindowFromPoint(current, clientPt);
            if (child == IntPtr.Zero || child == current)
            {
                break;
            }

            current = child;
        }

        return current;
    }

    /// <inheritdoc />
    public CapturedFrame CaptureScreenRectPixels(int physicalX, int physicalY, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Capture rect has zero area.", nameof(width));
        }

        _ = Win32.DwmFlush();

        IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC(NULL) failed.");
        }

        IntPtr memDc = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr old = IntPtr.Zero;
        try
        {
            memDc = Win32.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleDC failed.");
            }

            var bmi = CaptureBuffers.CreateTopDownBgraInfo(width, height);
            dib = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDIBSection failed.");
            }

            old = Win32.SelectObject(memDc, dib);
            if (!Win32.BitBlt(
                    memDc,
                    0,
                    0,
                    width,
                    height,
                    screenDc,
                    physicalX,
                    physicalY,
                    Win32.SrcCopy | (int)Win32.CaptureBlt))
            {
                throw new InvalidOperationException("BitBlt failed.");
            }

            if (old != IntPtr.Zero)
            {
                _ = Win32.SelectObject(memDc, old);
                old = IntPtr.Zero;
            }

            int stride = CaptureBuffers.BgraStride(width);
            var frame = new CapturedFrame(dib, bits, width, height, stride);
            dib = IntPtr.Zero;
            return frame;
        }
        finally
        {
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
