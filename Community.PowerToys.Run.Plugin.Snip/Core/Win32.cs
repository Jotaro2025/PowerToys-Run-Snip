// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Win32 PInvoke surface used by the Windows capture implementation.
/// Most signatures are source-generated (<see cref="LibraryImportAttribute"/>)
/// to avoid runtime marshal stubs; classic <see cref="DllImportAttribute"/>
/// remains only where the generator cannot express the signature
/// (StringBuilder buffers, enum delegates, ByValTStr structs).
/// </summary>
internal static partial class Win32
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

    // Generator limitation: managed function-pointer parameters for enum
    // callbacks (would need unsafe fnptr marshaling); keep the classic stub.
    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    // Generator limitation: MONITORINFOEX embeds a ByValTStr string, which the
    // source generator cannot marshal; keep the classic stub.
    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [LibraryImport(User32)]
    internal static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport(User32)]
    internal static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport(Gdi32, SetLastError = true)]
    internal static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport(Gdi32, SetLastError = true)]
    internal static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    /// <summary>
    /// Device-independent bitmap whose bits pointer is written by GDI (e.g. BitBlt).
    /// Use negative <c>biHeight</c> for top-down BGRA matching WPF.
    /// </summary>
    [LibraryImport(Gdi32, SetLastError = true)]
    internal static partial IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFO pbmi,
        uint iUsage,
        out IntPtr ppvBits,
        IntPtr hSection,
        uint dwOffset);

    [LibraryImport(Gdi32, SetLastError = true)]
    internal static partial IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr hObject);

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BitBlt(
        IntPtr hdcDest,
        int nXDest,
        int nYDest,
        int nWidth,
        int nHeight,
        IntPtr hdcSrc,
        int nXSrc,
        int nYSrc,
        int dwRop);

    [LibraryImport(Gdi32, SetLastError = true)]
    internal static partial int GetDIBits(
        IntPtr hdc,
        IntPtr hbm,
        uint start,
        uint lines,
        IntPtr lpvBits,
        ref BITMAPINFO lpbmi,
        uint usage);

    [LibraryImport(Kernel32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryPerformanceFrequency(out long lpFrequency);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCursorPos(int x, int y);

    [LibraryImport(Shcore, SetLastError = true)]
    internal static partial int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>Sets per-thread DPI awareness; returns the previous context (-1 on failure).</summary>
    [LibraryImport(User32, SetLastError = true)]
    internal static partial IntPtr SetThreadDpiAwarenessContext(IntPtr value);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 (-4).</summary>
    public static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public const uint MonitorDefaultToNearest = 2;

    [LibraryImport(User32)]
    internal static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport(User32)]
    internal static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [LibraryImport(User32)]
    internal static partial IntPtr MonitorFromRect(ref RECT rect, uint dwFlags);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport(User32, SetLastError = true)]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(IntPtr hWnd);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport(Kernel32)]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport(User32, EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>PrintWindow: copy window into DC. Use with <see cref="PwRenderFullContent"/> for DWM/Chromium.</summary>
    public const uint PwClientOnly = 0x00000001;

    /// <summary>Windows 8.1+: render full DWM content (needed for Edge/Chrome).</summary>
    public const uint PwRenderFullContent = 0x00000002;

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr hWnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    /// <summary>Hide the window (not destroyed).</summary>
    public const int SwHide = 0;

    /// <summary>Show without activating (keep underlay focus during scroll).</summary>
    public const int SwShowNoActivate = 4;

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const uint GaParent = 1;

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint WmMouseWheel = 0x020A;
    public const int WheelDelta = 120;
    public const uint GaRoot = 2;
    public const int RgnDiff = 4;

    [LibraryImport(User32)]
    internal static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport(User32)]
    internal static partial IntPtr ChildWindowFromPoint(IntPtr hWndParent, POINT point);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [LibraryImport(User32)]
    internal static partial IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [LibraryImport(User32, EntryPoint = "SendMessageW", SetLastError = true)]
    internal static partial IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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

    [LibraryImport(User32, SetLastError = true)]
    internal static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // Generator limitation: StringBuilder parameter; keep the classic stub.
    [DllImport(User32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [LibraryImport(User32, SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport(Gdi32)]
    internal static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport(Gdi32)]
    internal static partial int CombineRgn(IntPtr hrgnDst, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int iMode);

    [LibraryImport(User32)]
    internal static partial int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmFlush();

    public const int GwHwndNext = 2;
    public const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExLayered = 0x00080000;

    [LibraryImport(User32)]
    internal static partial IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [LibraryImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int WmHotkey = 0x0312;

    /// <summary>High bit set while the key is down.</summary>
    [LibraryImport(User32)]
    internal static partial short GetAsyncKeyState(int vKey);

    public const int DwmwaExtendedFrameBounds = 9;
    public const int DwmwaCloaked = 14;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // Generator limitation: enum-callback delegate parameter; keep the classic stub.
    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport(User32)]
    internal static partial IntPtr RealChildWindowFromPoint(IntPtr hwndParent, POINT ptParentClientCoords);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
