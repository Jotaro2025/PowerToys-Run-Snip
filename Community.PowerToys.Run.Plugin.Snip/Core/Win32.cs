// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Win32 PInvoke surface used by the Windows capture implementation.
/// Classic <see cref="DllImportAttribute"/> is used throughout for maximum
/// compatibility with structs containing embedded variable-length strings.
/// </summary>
internal static class Win32
{
    private const string User32 = "user32.dll";
    private const string Gdi32 = "gdi32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Shcore = "Shcore.dll";

    public const int MdtEffectiveDpi = 0;
    public const int SrcCopy = 0x00CC0020;
    public const uint CaptureBlt = 0x40000000;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoRedraw = 0x0008;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpShowWindow = 0x0040;
    public static readonly IntPtr HwndTopmost = new(-1);
    public const uint GmemMoveable = 0x2000;
    public const int DibRgbColors = 0;
    public const int BiRgb = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int CbSize;

        public RECT RcMonitor;

        public RECT RcWork;

        public uint DwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string SzDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;

        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
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

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER BmiHeader;

        public uint BmiColors; // padding
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport(User32)]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport(User32)]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    /// <summary>
    /// Device-independent bitmap whose bits pointer is written by GDI (e.g. BitBlt).
    /// Use negative <c>biHeight</c> for top-down BGRA matching WPF.
    /// </summary>
    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO pbmi,
        uint iUsage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint dwOffset);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(
        IntPtr hdcDest,
        int nXDest,
        int nYDest,
        int nWidth,
        int nHeight,
        IntPtr hdcSrc,
        int nXSrc,
        int nYSrc,
        int dwRop);

    [DllImport(Gdi32, SetLastError = true)]
    public static extern int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint lines,
        IntPtr lpvBits,
        ref BITMAPINFO lpbmi,
        uint usage);

    [DllImport(Kernel32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryPerformanceFrequency(out long lpFrequency);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport(Shcore, SetLastError = true)]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>Sets per-thread DPI awareness; returns the previous context (-1 on failure).</summary>
    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 (-4).</summary>
    public static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public const uint MonitorDefaultToNearest = 2;

    [DllImport(User32)]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport(Kernel32)]
    public static extern uint GetCurrentThreadId();

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>PrintWindow: copy window into DC. Use with <see cref="PwRenderFullContent"/> for DWM/Chromium.</summary>
    public const uint PwClientOnly = 0x00000001;

    /// <summary>Windows 8.1+: render full DWM content (needed for Edge/Chrome).</summary>
    public const uint PwRenderFullContent = 0x00000002;

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>Hide the window (not destroyed).</summary>
    public const int SwHide = 0;

    /// <summary>Show without activating (keep underlay focus during scroll).</summary>
    public const int SwShowNoActivate = 4;

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const uint GaParent = 1;

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint WmMouseWheel = 0x020A;
    public const int WheelDelta = 120;
    public const uint GaRoot = 2;
    public const int RgnDiff = 4;

    [DllImport(User32)]
    public static extern IntPtr WindowFromPoint(POINT point);

    [DllImport(User32)]
    public static extern IntPtr ChildWindowFromPoint(IntPtr hWndParent, POINT point);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport(User32)]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint InputMouse = 0;
    public const uint MouseEventFMove = 0x0001;
    public const uint MouseEventFLeftDown = 0x0002;
    public const uint MouseEventFLeftUp = 0x0004;
    public const uint MouseEventFWheel = 0x0800;
    public const uint MouseEventFAbsolute = 0x8000;

    /// <summary>
    /// Native <c>MOUSEINPUT</c>. On x64 <c>dwExtraInfo</c> forces 8-byte alignment
    /// (4 bytes padding after <c>time</c>) so the struct is 32 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int Dx;

        public int Dy;

        public uint MouseData;

        public uint DwFlags;

        public uint Time;

        public IntPtr DwExtraInfo;
    }

    /// <summary>
    /// Keyboard half of the INPUT union (needed so the union is sized like Win32).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort WVk;

        public ushort WScan;

        public uint DwFlags;

        public uint Time;

        public IntPtr DwExtraInfo;
    }

    /// <summary>Hardware half of the INPUT union.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint UMsg;

        public ushort WParamL;

        public ushort WParamH;
    }

    /// <summary>
    /// Win32 INPUT union. Explicit overlay of mi/ki/hi; size follows the largest
    /// member (<see cref="MOUSEINPUT"/> = 32 on x64).
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT Mi;

        [FieldOffset(0)]
        public KEYBDINPUT Ki;

        [FieldOffset(0)]
        public HARDWAREINPUT Hi;
    }

    /// <summary>
    /// Win32 <c>INPUT</c>. Sequential layout adds 4 bytes of padding after
    /// <c>type</c> on x64 so the union is 8-byte aligned (total 40). On x86
    /// there is no padding (total 28). Always pass <see cref="InputSize"/> to
    /// <c>SendInput</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint Type;

        public INPUTUNION U;
    }

    /// <summary>Cached <c>sizeof(INPUT)</c> for <c>SendInput</c> cbSize.</summary>
    public static readonly int InputSize = Marshal.SizeOf<INPUT>();

    [DllImport(User32, SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport(User32, SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport(Gdi32)]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport(Gdi32)]
    public static extern int CombineRgn(IntPtr hrgnDst, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int iMode);

    [DllImport(User32)]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    public const int GwHwndNext = 2;
    public const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExLayered = 0x00080000;

    [DllImport(User32)]
    public static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int WmHotkey = 0x0312;

    /// <summary>High bit set while the key is down.</summary>
    [DllImport(User32)]
    public static extern short GetAsyncKeyState(int vKey);

    public const int DwmwaExtendedFrameBounds = 9;
    public const int DwmwaCloaked = 14;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport(User32)]
    public static extern IntPtr RealChildWindowFromPoint(IntPtr hwndParent, POINT ptParentClientCoords);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
