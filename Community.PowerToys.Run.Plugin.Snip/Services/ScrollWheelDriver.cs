// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Wheel dispatch for the scroll-capture loop: keep the cursor held inside the
/// selection (Chromium requires hover), then deliver one wheel step via
/// SendInput/message, falling back to UIA ScrollPattern. Owns pointer-hold
/// begin/end so the controller just calls <see cref="SendWheel"/> and
/// <see cref="ReleasePointer"/>.
/// </summary>
internal sealed class ScrollWheelDriver
{
    private readonly Int32Rect _captureRect;
    private readonly IntPtr _overlayHwnd;
    private readonly bool _scrollUp;
    private readonly int _stepsPerTick;

    public ScrollWheelDriver(Int32Rect captureRect, IntPtr overlayHwnd, bool scrollUp, int stepsPerTick)
    {
        _captureRect = captureRect;
        _overlayHwnd = overlayHwnd;
        _scrollUp = scrollUp;
        _stepsPerTick = Math.Max(1, stepsPerTick);
    }

    /// <summary>Selection center in physical pixels (wheel target and pointer-hold point).</summary>
    public (int X, int Y) Center
    {
        get
        {
            int cx = _captureRect.X + (_captureRect.Width / 2);
            int cy = _captureRect.Y + (_captureRect.Height / 2);
            return (cx, cy);
        }
    }

    /// <summary>
    /// Delivers one wheel step at the selection center. Ensures the pointer is
    /// held inside the hole first. True when any path (SendInput, message, UIA)
    /// reports the wheel was accepted.
    /// </summary>
    public bool SendWheel()
    {
        var (cx, cy) = Center;
        int delta = (_scrollUp ? 1 : -1) * Win32.WheelDelta * _stepsPerTick;

        // Keep cursor over the hole for the whole session (Chromium needs it).
        if (!Win32CaptureService.IsScrollPointerHeld)
        {
            BeginPointerHold();
        }

        if (Win32CaptureService.TrySendWheelAt(cx, cy, delta, _overlayHwnd))
        {
            return true;
        }

        // SendInput / message path failed — try UIA ScrollPattern on the MTA worker.
        if (TryUiaScrollAt(cx, cy, delta))
        {
            SnipVerbose.Scroll($"[scroll] SimulateWheel via UIA ScrollPattern at ({cx},{cy})");
            return true;
        }

        Log.Warn(
            $"Snip scroll SimulateWheel failed at ({cx},{cy}) — SendInput/UIA unavailable",
            typeof(ScrollWheelDriver));
        SnipVerbose.Scroll($"[scroll] SimulateWheel failed at ({cx},{cy})");
        return false;
    }

    /// <summary>Starts the pointer hold at the selection center (focus click included).</summary>
    public void BeginPointerHold()
    {
        var (cx, cy) = Center;
        try
        {
            Win32CaptureService.BeginScrollPointerHold(
                cx,
                cy,
                injectFocusClick: true,
                skipOverlayHwnd: _overlayHwnd);
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll BeginScrollPointerHold failed: {ex.Message}", typeof(ScrollWheelDriver));
        }
    }

    /// <summary>Releases the pointer hold at session end.</summary>
    public static void ReleasePointer()
    {
        try
        {
            Win32CaptureService.EndScrollPointerHold();
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll EndScrollPointerHold failed: {ex.Message}", typeof(ScrollWheelDriver));
        }
    }

    private static bool TryUiaScrollAt(int physicalX, int physicalY, int delta)
    {
        try
        {
            // UiaScrollFallback owns a dedicated MTA worker: UIA provider calls no
            // longer round-trip through the UI STA (they used to freeze the UI when
            // a slow provider took hundreds of ms), so the capture loop (already on
            // the thread pool) can call straight in and just wait for the result.
            return UiaScrollFallback.TryScrollAt(physicalX, physicalY, delta);
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll UIA invoke failed: {ex.Message}", typeof(ScrollWheelDriver));
            return false;
        }
    }
}
