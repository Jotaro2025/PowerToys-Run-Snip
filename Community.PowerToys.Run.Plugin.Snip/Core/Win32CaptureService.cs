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
            DeviceName: "VirtualDesktop",
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

    /// <summary>Returns the monitor under the cursor, or primary / first physical screen.</summary>
    public static ScreenInfo GetScreenAtCursor(IReadOnlyList<ScreenInfo> screens)
    {
        var physical = screens.Where(s => s.DeviceName != "VirtualDesktop").ToList();
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

    public WriteableBitmap CaptureScreen(ScreenInfo screen)
        => CaptureScreenPixels(screen).ToWriteableBitmap();

    /// <summary>
    /// BitBlt into a top-down BGRA <c>CreateDIBSection</c>, then one copy into a
    /// managed <see cref="CapturedFrame"/> buffer. Safe off the UI thread —
    /// no <see cref="WriteableBitmap"/> is created here.
    /// Avoids the old AllocHGlobal + Marshal.Copy staging buffer (2–3× peak).
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

            // Top-down 32bpp DIB: BitBlt writes final pixels here (no GetDIBits
            // into a second native buffer). Bounds are physical pixels (per-monitor DPI).
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

            int stride = CaptureBuffers.BgraStride(w);
            int bufLen = CaptureBuffers.BgraByteCount(w, h);
            var pixels = new byte[bufLen];

            // Single copy: DIB bits → managed CapturedFrame (thread-pool safe for #16).
            Marshal.Copy(bits, pixels, 0, bufLen);
            return new CapturedFrame(pixels, w, h, stride);
        }
        finally
        {
            if (old != IntPtr.Zero && memDc != IntPtr.Zero)
            {
                Win32.SelectObject(memDc, old);
            }

            if (dib != IntPtr.Zero)
            {
                Win32.DeleteObject(dib);
            }

            if (memDc != IntPtr.Zero)
            {
                Win32.DeleteDC(memDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                _ = Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
