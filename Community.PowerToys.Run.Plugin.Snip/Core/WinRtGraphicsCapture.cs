// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Windows.Graphics.Capture entry points used by the snip pipeline. The actual
/// ABI work lives in <see cref="WinRtCaptureNative"/> (raw COM, no CsWinRT) so
/// capture keeps working inside PowerToys Run, whose host loads its own
/// <c>WinRT.Runtime</c>. Monitors/windows on an HDR desktop are captured as
/// FP16 scRGB and tone-mapped to SDR; SDR displays take the cheaper BGRA8 path
/// and are copied unchanged.
/// </summary>
/// <remarks>
/// Limitations:
/// <list type="bullet">
/// <item>Requires Windows 10 1903+; failures fall back to GDI.</item>
/// <item>One monitor or one window per capture — the multi-monitor union is
/// composed by <see cref="PreferWinRtCaptureService"/> from per-monitor frames.</item>
/// <item>Protected / DRM content may yield black frames; GDI is then used.</item>
/// </list>
/// </remarks>
internal static class WinRtGraphicsCapture
{
    private static readonly object LogLock = new();
    private static bool? _supported;
    private static bool _loggedHdrPath;
    private static bool _loggedBackend;

    /// <summary>True when the OS reports Graphics Capture is available.</summary>
    public static bool IsSupported
    {
        get
        {
            if (_supported.HasValue)
            {
                return _supported.Value;
            }

            try
            {
                _supported = WinRtCaptureNative.IsSupportedPlatform;
            }
            catch (Exception ex)
            {
                Log.Warn($"Snip WinRT Graphics Capture probe failed: {ex.Message}", typeof(WinRtGraphicsCapture));
                _supported = false;
            }

            return _supported.Value;
        }
    }

    /// <summary>Test hook: ensure the capture gate can be entered (not held).</summary>
    internal static bool CaptureGateAvailableForTests => WinRtCaptureNative.CaptureGateAvailableForTests;

    /// <summary>
    /// Creates the D3D device and activation factories ahead of the first
    /// capture so the first frame skips that cost. Safe no-op when unsupported.
    /// </summary>
    public static void PrewarmDevice()
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            WinRtCaptureNative.Prewarm();
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip WinRT device prewarm failed (will retry on first capture): {ex.Message}", typeof(WinRtGraphicsCapture));
        }
    }

    /// <summary>
    /// Capture a physical monitor (HMONITOR) into a managed BGRA <see cref="CapturedFrame"/>.
    /// Returns false on any failure (caller should use GDI).
    /// </summary>
    public static bool TryCaptureMonitor(IntPtr hMonitor, bool captureCursor, out CapturedFrame frame)
    {
        frame = null;
        if (hMonitor == IntPtr.Zero || !IsSupported)
        {
            return false;
        }

        HdrDisplayState state = GetStateForMonitor(hMonitor);
        bool captured = WinRtCaptureNative.TryCapture(
            hMonitor,
            isWindow: false,
            captureCursor,
            cropX: 0,
            cropY: 0,
            cropWidth: 0,
            cropHeight: 0,
            state.WhiteScale,
            state.NeedsToneMapping,
            out frame);
        LogFirstSuccess("monitor", state.NeedsToneMapping, captured ? frame : null);
        return captured;
    }

    /// <summary>
    /// Capture a physical-pixel rectangle that lies inside <paramref name="hMonitor"/>.
    /// The monitor is captured natively and only the sub-rect is read back, which
    /// keeps scroll tiles / GIF frames cheap on HDR displays.
    /// </summary>
    public static bool TryCaptureMonitorRect(
        IntPtr hMonitor,
        int physicalX,
        int physicalY,
        int width,
        int height,
        out CapturedFrame frame)
    {
        frame = null;
        if (hMonitor == IntPtr.Zero || !IsSupported || width <= 0 || height <= 0)
        {
            return false;
        }

        var info = new Win32.MONITORINFOEX { CbSize = Marshal.SizeOf<Win32.MONITORINFOEX>() };
        if (!Win32.GetMonitorInfo(hMonitor, ref info))
        {
            return false;
        }

        HdrDisplayState state = GetStateForMonitor(hMonitor);
        return WinRtCaptureNative.TryCapture(
            hMonitor,
            isWindow: false,
            captureCursor: false,
            cropX: physicalX - info.RcMonitor.Left,
            cropY: physicalY - info.RcMonitor.Top,
            width,
            height,
            state.WhiteScale,
            state.NeedsToneMapping,
            out frame);
    }

    /// <summary>
    /// Capture a top-level window (including its non-client area) into a managed
    /// BGRA <see cref="CapturedFrame"/>. Returns false on any failure.
    /// </summary>
    public static bool TryCaptureWindow(IntPtr hwnd, bool captureCursor, out CapturedFrame frame)
    {
        frame = null;
        if (hwnd == IntPtr.Zero || !IsSupported)
        {
            return false;
        }

        HdrDisplayState state = HdrDisplayInfo.GetForWindow(hwnd);
        bool captured = WinRtCaptureNative.TryCapture(
            hwnd,
            isWindow: true,
            captureCursor,
            cropX: 0,
            cropY: 0,
            cropWidth: 0,
            cropHeight: 0,
            state.WhiteScale,
            state.NeedsToneMapping,
            out frame);
        LogFirstSuccess("window", state.NeedsToneMapping, captured ? frame : null);
        return captured;
    }

    /// <summary>
    /// Capture a window-local physical-pixel rectangle from a top-level window.
    /// </summary>
    public static bool TryCaptureWindowRect(
        IntPtr hwnd,
        int localX,
        int localY,
        int width,
        int height,
        out CapturedFrame frame)
    {
        frame = null;
        if (hwnd == IntPtr.Zero || !IsSupported || width <= 0 || height <= 0)
        {
            return false;
        }

        HdrDisplayState state = HdrDisplayInfo.GetForWindow(hwnd);
        return WinRtCaptureNative.TryCapture(
            hwnd,
            isWindow: true,
            captureCursor: false,
            cropX: localX,
            cropY: localY,
            cropWidth: width,
            cropHeight: height,
            state.WhiteScale,
            state.NeedsToneMapping,
            out frame);
    }

    /// <summary>True when the monitor uses the scene-referred HDR composition path.</summary>
    internal static bool NeedsHdrToneMapping(IntPtr hMonitor)
        => GetStateForMonitor(hMonitor).NeedsToneMapping;

    private static HdrDisplayState GetStateForMonitor(IntPtr hMonitor)
    {
        HdrDisplayState state = HdrDisplayInfo.GetForMonitor(hMonitor);
        if (state.NeedsToneMapping)
        {
            lock (LogLock)
            {
                if (!_loggedHdrPath)
                {
                    _loggedHdrPath = true;
                    Log.Info(
                        $"Snip HDR capture path active: SDR white raw={state.SdrWhiteLevelRaw} scale={state.WhiteScale:F3}",
                        typeof(WinRtGraphicsCapture));
                }
            }
        }

        return state;
    }

    /// <summary>
    /// Logs the active capture backend once per process so support logs show
    /// whether WinRT capture (and the HDR path) is actually in use.
    /// </summary>
    private static void LogFirstSuccess(string kind, bool toneMapped, CapturedFrame frame)
    {
        if (frame == null)
        {
            return;
        }

        lock (LogLock)
        {
            if (_loggedBackend)
            {
                return;
            }

            _loggedBackend = true;
        }

        Log.Info(
            $"Snip WinRT capture active: {kind} {frame.Width}x{frame.Height} {(toneMapped ? "FP16->SDR" : "BGRA8")}",
            typeof(WinRtGraphicsCapture));
    }
}
