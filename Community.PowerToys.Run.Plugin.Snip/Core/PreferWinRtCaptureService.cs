// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Prefers WinRT Graphics Capture for whole-monitor frames when available;
/// always falls back to <see cref="Win32CaptureService"/> (GDI BitBlt).
/// Sub-rectangle / scroll tiles stay on GDI for latency.
/// </summary>
public sealed class PreferWinRtCaptureService : ICaptureService
{
    private readonly Win32CaptureService _gdi = new Win32CaptureService();

    /// <summary>Underlying GDI service (tests / explicit fallback).</summary>
    internal Win32CaptureService Gdi => _gdi;

    public IReadOnlyList<ScreenInfo> GetScreens() => _gdi.GetScreens();

    public WriteableBitmap CaptureScreen(ScreenInfo screen)
    {
        using var frame = CaptureScreenPixels(screen);
        return frame.ToWriteableBitmap();
    }

    public CapturedFrame CaptureScreenPixels(ScreenInfo screen)
    {
        ArgumentNullException.ThrowIfNull(screen);

        // Virtual desktop / synthetic ActiveWindow entries have no HMONITOR — GDI only.
        if (screen.Handle != IntPtr.Zero
            && screen.DeviceName != Win32CaptureService.VirtualDesktopName
            && WinRtGraphicsCapture.TryCaptureMonitor(screen.Handle, captureCursor: true, out var winrt)
            && winrt != null)
        {
            return winrt;
        }

        return _gdi.CaptureScreenPixels(screen);
    }

    public CapturedFrame CaptureScreenRectPixels(int physicalX, int physicalY, int width, int height)
        => _gdi.CaptureScreenRectPixels(physicalX, physicalY, width, height);
}
