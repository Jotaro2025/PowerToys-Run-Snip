// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

using Wox.Plugin.Logger;

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

    /// <summary>Settle delay after SetCursorPos / focus click before wheel (Chromium hit-test).</summary>
    internal const int ScrollPointerSettleMs = 80;

    /// <summary>Expected <c>sizeof(INPUT)</c>: 40 on x64, 28 on x86.</summary>
    internal static int ExpectedSendInputStructSize
        => IntPtr.Size == 8 ? 40 : 28;

    private static readonly object ScrollPointerLock = new();
    private static bool _scrollPointerHeld;
    private static Win32.POINT _scrollPointerSaved;
    private static bool _scrollPointerHadSaved;
    private static bool _scrollFocusClickDone;

    /// <summary>True while scroll capture holds the cursor over the selection center.</summary>
    internal static bool IsScrollPointerHeld
    {
        get
        {
            lock (ScrollPointerLock)
            {
                return _scrollPointerHeld;
            }
        }
    }

    /// <summary>
    /// Move the cursor to the selection center and keep it there until
    /// <see cref="EndScrollPointerHold"/>. Optionally injects a left click so
    /// Chromium/Edge take focus before the first wheel. When
    /// <paramref name="skipOverlayHwnd"/> is set, briefly marks the overlay
    /// <c>WS_EX_TRANSPARENT</c> for the click so hit-testing reaches the underlay
    /// even if <c>SetWindowRgn</c> hole hit-test is incomplete.
    /// </summary>
    public static void BeginScrollPointerHold(
        int physicalX,
        int physicalY,
        bool injectFocusClick = true,
        IntPtr skipOverlayHwnd = default)
    {
        lock (ScrollPointerLock)
        {
            if (!_scrollPointerHeld)
            {
                _scrollPointerHadSaved = Win32.GetCursorPos(out _scrollPointerSaved);
                _scrollPointerHeld = true;
                _scrollFocusClickDone = false;
            }
        }

        _ = Win32.SetCursorPos(physicalX, physicalY);
        Thread.Sleep(ScrollPointerSettleMs);

        var pt = new Win32.POINT { X = physicalX, Y = physicalY };
        IntPtr target = ResolveScrollTargetAt(pt, skipOverlayHwnd);
        _ = TryActivateScrollTarget(target);

        if (injectFocusClick)
        {
            bool clicked;
            lock (ScrollPointerLock)
            {
                clicked = !_scrollFocusClickDone;
                if (clicked)
                {
                    _scrollFocusClickDone = true;
                }
            }

            if (clicked)
            {
                bool overlayCovers = IsOverlayCoveringPoint(pt, skipOverlayHwnd);
                using (TemporaryOverlayTransparent(skipOverlayHwnd, force: overlayCovers || skipOverlayHwnd != IntPtr.Zero))
                {
                    _ = Win32.SetCursorPos(physicalX, physicalY);
                    Thread.Sleep(ScrollPointerSettleMs / 2);
                    if (TrySendInputFocusClick())
                    {
                        Log.Info(
                            $"Snip scroll focus click via SendInput at ({physicalX},{physicalY}) overlayCovers={overlayCovers} target={DescribeHwndClass(target)}",
                            typeof(Win32CaptureService));
                        Thread.Sleep(ScrollPointerSettleMs);
                        _ = TryActivateScrollTarget(target);
                    }
                }
            }
        }
    }

    /// <summary>Restore the cursor saved by <see cref="BeginScrollPointerHold"/> (no-op if not held).</summary>
    public static void EndScrollPointerHold()
    {
        Win32.POINT saved = default;
        bool restore = false;
        lock (ScrollPointerLock)
        {
            if (!_scrollPointerHeld)
            {
                return;
            }

            restore = _scrollPointerHadSaved;
            saved = _scrollPointerSaved;
            _scrollPointerHeld = false;
            _scrollPointerHadSaved = false;
            _scrollFocusClickDone = false;
        }

        if (restore)
        {
            _ = Win32.SetCursorPos(saved.X, saved.Y);
        }
    }

    /// <summary>
    /// Deliver a mouse wheel tick at a screen point under the selection hole.
    /// Prefers <c>SendInput(MOUSEEVENTF_WHEEL)</c> while the overlay is briefly
    /// <c>WS_EX_TRANSPARENT</c> so the OS hit-tests Edge/Chromium (hole alone is
    /// often insufficient for Topmost WPF). Activates the underlay, then
    /// <c>PostMessage(WM_MOUSEWHEEL)</c> as a secondary nudge. Falls back to
    /// SendMessage when SendInput fails.
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
        string targetClass = DescribeHwndClass(target);
        uint targetPid = 0;
        if (target != IntPtr.Zero)
        {
            _ = Win32.GetWindowThreadProcessId(target, out targetPid);
        }

        bool holding;
        lock (ScrollPointerLock)
        {
            holding = _scrollPointerHeld;
        }

        bool moved = false;
        Win32.POINT saved = default;
        if (!holding)
        {
            if (Win32.GetCursorPos(out saved)
                && (saved.X != physicalX || saved.Y != physicalY)
                && Win32.SetCursorPos(physicalX, physicalY))
            {
                moved = true;
            }
            else if (saved.X != physicalX || saved.Y != physicalY)
            {
                _ = Win32.SetCursorPos(physicalX, physicalY);
            }
        }
        else
        {
            _ = Win32.SetCursorPos(physicalX, physicalY);
        }

        // Chromium often ignores a wheel that arrives before hit-testing settles.
        Thread.Sleep(ScrollPointerSettleMs);

        bool overlayCovers = IsOverlayCoveringPoint(pt, skipOverlayHwnd);
        _ = TryActivateScrollTarget(target);

        try
        {
            using (TemporaryOverlayTransparent(skipOverlayHwnd, force: skipOverlayHwnd != IntPtr.Zero))
            {
                _ = Win32.SetCursorPos(physicalX, physicalY);
                Thread.Sleep(ScrollPointerSettleMs / 4);

                if (TrySendInputWheel(delta))
                {
                    // Real input first; PostMessage is a secondary nudge after focus.
                    if (target != IntPtr.Zero)
                    {
                        IntPtr wParam = PackWheelWParam(delta);
                        IntPtr lParam = PackWheelLParam(physicalX, physicalY);
                        _ = Win32.PostMessage(target, Win32.WmMouseWheel, wParam, lParam);
                    }

                    Log.Info(
                        $"Snip scroll wheel via SendInput at ({physicalX},{physicalY}) delta={delta} target={targetClass} pid={targetPid} overlayCovers={overlayCovers}",
                        typeof(Win32CaptureService));
                    return true;
                }
            }

            int sendInputErr = Marshal.GetLastWin32Error();
            if (target == IntPtr.Zero)
            {
                Log.Warn(
                    $"Snip scroll SendInput failed (err={sendInputErr}) and no underlay HWND at ({physicalX},{physicalY}) overlayCovers={overlayCovers}",
                    typeof(Win32CaptureService));
                return false;
            }

            IntPtr wParamFb = PackWheelWParam(delta);
            IntPtr lParamFb = PackWheelLParam(physicalX, physicalY);
            _ = Win32.SendMessage(target, Win32.WmMouseWheel, wParamFb, lParamFb);
            Log.Warn(
                $"Snip scroll wheel fallback SendMessage at ({physicalX},{physicalY}) delta={delta} target={targetClass} pid={targetPid} overlayCovers={overlayCovers} (SendInput err={sendInputErr})",
                typeof(Win32CaptureService));
            return true;
        }
        finally
        {
            if (moved && !holding)
            {
                _ = Win32.SetCursorPos(saved.X, saved.Y);
            }
        }
    }

    /// <summary>
    /// AttachThreadInput + BringWindowToTop + SetForegroundWindow so Chromium
    /// accepts the following wheel (foreground-lock safe best-effort).
    /// </summary>
    internal static bool TryActivateScrollTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr root = Win32.GetAncestor(hwnd, Win32.GaRoot);
        if (root == IntPtr.Zero)
        {
            root = hwnd;
        }

        IntPtr foreground = Win32.GetForegroundWindow();
        if (foreground == root)
        {
            return true;
        }

        uint ourTid = Win32.GetCurrentThreadId();
        uint fgTid = 0;
        if (foreground != IntPtr.Zero)
        {
            fgTid = Win32.GetWindowThreadProcessId(foreground, out _);
        }

        uint targetTid = Win32.GetWindowThreadProcessId(root, out _);
        bool attachedFg = false;
        bool attachedTarget = false;
        try
        {
            if (fgTid != 0 && fgTid != ourTid)
            {
                attachedFg = Win32.AttachThreadInput(ourTid, fgTid, true);
            }

            if (targetTid != 0 && targetTid != ourTid)
            {
                attachedTarget = Win32.AttachThreadInput(ourTid, targetTid, true);
            }

            _ = Win32.BringWindowToTop(root);
            bool ok = Win32.SetForegroundWindow(root);
            if (!ok)
            {
                Log.Warn(
                    $"Snip scroll SetForegroundWindow failed for {DescribeHwndClass(root)} (err={Marshal.GetLastWin32Error()})",
                    typeof(Win32CaptureService));
            }

            return ok;
        }
        finally
        {
            if (attachedTarget)
            {
                _ = Win32.AttachThreadInput(ourTid, targetTid, false);
            }

            if (attachedFg)
            {
                _ = Win32.AttachThreadInput(ourTid, fgTid, false);
            }
        }
    }

    /// <summary>True when <see cref="Win32.WindowFromPoint"/> hits the snip overlay at <paramref name="pt"/>.</summary>
    internal static bool IsOverlayCoveringPoint(Win32.POINT pt, IntPtr skipOverlayHwnd)
    {
        if (skipOverlayHwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr hit = Win32.WindowFromPoint(pt);
        return IsOwnedByOverlay(hit, skipOverlayHwnd);
    }

    /// <summary>
    /// Briefly OR <c>WS_EX_TRANSPARENT</c> onto the overlay so SendInput hit-tests
    /// the underlay. No-op when <paramref name="overlayHwnd"/> is zero or <paramref name="force"/> is false.
    /// </summary>
    private static TemporaryStyleScope TemporaryOverlayTransparent(IntPtr overlayHwnd, bool force)
    {
        if (!force || overlayHwnd == IntPtr.Zero)
        {
            return TemporaryStyleScope.None;
        }

        IntPtr prev = Win32.GetWindowLongPtr(overlayHwnd, Win32.GwlExStyle);
#pragma warning disable CA2020 // OR of style bits into IntPtr; same pattern as WM_MOUSEWHEEL packing.
        _ = Win32.SetWindowLongPtr(
            overlayHwnd,
            Win32.GwlExStyle,
            unchecked((IntPtr)(prev.ToInt64() | Win32.WsExTransparent)));
#pragma warning restore CA2020
        return new TemporaryStyleScope(overlayHwnd, prev);
    }

    private readonly struct TemporaryStyleScope : IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly IntPtr _prev;
        private readonly bool _active;

        public static TemporaryStyleScope None => default;

        public TemporaryStyleScope(IntPtr hwnd, IntPtr prev)
        {
            _hwnd = hwnd;
            _prev = prev;
            _active = hwnd != IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_active)
            {
                _ = Win32.SetWindowLongPtr(_hwnd, Win32.GwlExStyle, _prev);
            }
        }
    }

    /// <summary>Pack signed wheel delta into <c>WM_MOUSEWHEEL</c> WPARAM (high word).</summary>
    internal static IntPtr PackWheelWParam(int delta)
    {
#pragma warning disable CA2020 // WM_MOUSEWHEEL packs signed delta into WPARAM bits.
        return unchecked((IntPtr)((delta << 16) & 0xFFFFFFFF));
#pragma warning restore CA2020
    }

    /// <summary>Pack screen coords into <c>WM_MOUSEWHEEL</c> LPARAM.</summary>
    internal static IntPtr PackWheelLParam(int physicalX, int physicalY)
    {
#pragma warning disable CA2020 // WM_MOUSEWHEEL packs coords into LPARAM bits.
        return unchecked((IntPtr)((uint)(((physicalY & 0xFFFF) << 16) | (physicalX & 0xFFFF))));
#pragma warning restore CA2020
    }

    /// <summary>Pack signed wheel delta for <c>MOUSEINPUT.mouseData</c> with <c>MOUSEEVENTF_WHEEL</c>.</summary>
    internal static uint PackSendInputWheelData(int delta)
        => unchecked((uint)delta);

    /// <summary>Build a mouse <see cref="Win32.INPUT"/> with the given flags / data.</summary>
    internal static Win32.INPUT CreateMouseInput(uint flags, uint mouseData = 0, int dx = 0, int dy = 0)
        => new()
        {
            Type = Win32.InputMouse,
            U = new Win32.INPUTUNION
            {
                Mi = new Win32.MOUSEINPUT
                {
                    Dx = dx,
                    Dy = dy,
                    MouseData = mouseData,
                    DwFlags = flags,
                    Time = 0,
                    DwExtraInfo = IntPtr.Zero,
                },
            },
        };

    /// <summary>Left click (DOWN+UP) via one <c>SendInput</c> array to focus the underlay.</summary>
    internal static bool TrySendInputFocusClick()
    {
        var inputs = new[]
        {
            CreateMouseInput(Win32.MouseEventFLeftDown),
            CreateMouseInput(Win32.MouseEventFLeftUp),
        };
        uint sent = Win32.SendInput((uint)inputs.Length, inputs, Win32.InputSize);
        return sent == (uint)inputs.Length;
    }

    /// <summary>Inject one wheel tick via <c>SendInput</c>. Returns false when the API reports no events.</summary>
    internal static bool TrySendInputWheel(int delta)
    {
        var inputs = new[]
        {
            CreateMouseInput(Win32.MouseEventFWheel, PackSendInputWheelData(delta)),
        };
        uint sent = Win32.SendInput((uint)inputs.Length, inputs, Win32.InputSize);
        return sent == (uint)inputs.Length;
    }

    private static string DescribeHwndClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "(none)";
        }

        var sb = new StringBuilder(256);
        int n = Win32.GetClassName(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : $"hwnd=0x{hwnd.ToInt64():X}";
    }

    /// <summary>
    /// HWND that should receive <c>WM_MOUSEWHEEL</c> at <paramref name="pt"/>.
    /// When the snip overlay (or a child) is on top, walk Z-order / briefly mark the
    /// overlay <c>WS_EX_TRANSPARENT</c> so the live window under the selection hole is used
    /// instead of returning <see cref="IntPtr.Zero"/> (which made every wheel fail).
    /// </summary>
    internal static IntPtr ResolveScrollTargetAt(Win32.POINT pt, IntPtr skipOverlayHwnd)
    {
        IntPtr hit = Win32.WindowFromPoint(pt);
        if (hit == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (skipOverlayHwnd != IntPtr.Zero && IsOwnedByOverlay(hit, skipOverlayHwnd))
        {
            hit = FindTopLevelBeneathOverlay(pt, skipOverlayHwnd);
            if (hit == IntPtr.Zero)
            {
                hit = WindowFromPointWithOverlayTransparent(pt, skipOverlayHwnd);
            }

            if (hit == IntPtr.Zero || IsOwnedByOverlay(hit, skipOverlayHwnd))
            {
                return IntPtr.Zero;
            }
        }

        IntPtr root = Win32.GetAncestor(hit, Win32.GaRoot);
        if (root == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (skipOverlayHwnd != IntPtr.Zero && IsOwnedByOverlay(root, skipOverlayHwnd))
        {
            return IntPtr.Zero;
        }

        return FindDeepestChildAt(root, pt);
    }

    /// <summary>True when <paramref name="hwnd"/> is the overlay or lives under its root.</summary>
    internal static bool IsOwnedByOverlay(IntPtr hwnd, IntPtr overlayHwnd)
    {
        if (hwnd == IntPtr.Zero || overlayHwnd == IntPtr.Zero)
        {
            return false;
        }

        if (hwnd == overlayHwnd)
        {
            return true;
        }

        IntPtr root = Win32.GetAncestor(hwnd, Win32.GaRoot);
        return root == overlayHwnd;
    }

    /// <summary>
    /// Next visible top-level window below <paramref name="overlayHwnd"/> whose rect contains <paramref name="pt"/>.
    /// </summary>
    internal static IntPtr FindTopLevelBeneathOverlay(Win32.POINT pt, IntPtr overlayHwnd)
    {
        for (IntPtr w = Win32.GetWindow(overlayHwnd, Win32.GwHwndNext);
             w != IntPtr.Zero;
             w = Win32.GetWindow(w, Win32.GwHwndNext))
        {
            if (!Win32.IsWindowVisible(w) || Win32.IsIconic(w))
            {
                continue;
            }

            if (!Win32.GetWindowRect(w, out Win32.RECT rc))
            {
                continue;
            }

            if (pt.X < rc.Left || pt.X >= rc.Right || pt.Y < rc.Top || pt.Y >= rc.Bottom)
            {
                continue;
            }

            return w;
        }

        return IntPtr.Zero;
    }

    private static IntPtr WindowFromPointWithOverlayTransparent(Win32.POINT pt, IntPtr overlayHwnd)
    {
        IntPtr prev = Win32.GetWindowLongPtr(overlayHwnd, Win32.GwlExStyle);
#pragma warning disable CA2020 // OR of style bits into IntPtr; same pattern as WM_MOUSEWHEEL packing.
        _ = Win32.SetWindowLongPtr(
            overlayHwnd,
            Win32.GwlExStyle,
            unchecked((IntPtr)(prev.ToInt64() | Win32.WsExTransparent)));
#pragma warning restore CA2020
        try
        {
            return Win32.WindowFromPoint(pt);
        }
        finally
        {
            _ = Win32.SetWindowLongPtr(overlayHwnd, Win32.GwlExStyle, prev);
        }
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
