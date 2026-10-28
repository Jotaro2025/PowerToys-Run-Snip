// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Prefers WinRT Graphics Capture for whole-monitor frames when available;
/// always falls back to <see cref="Win32CaptureService"/> (GDI BitBlt).
/// Sub-rectangle / scroll tiles stay on GDI for latency, except on HDR
/// monitors where <see cref="Win32CaptureService"/> routes through WGC to avoid
/// the washed-out 8-bit composition.
/// </summary>
public sealed class PreferWinRtCaptureService : ICaptureService
{
    private readonly Win32CaptureService _gdi = new();

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

        // Virtual desktop / synthetic ActiveWindow entries have no HMONITOR — GDI only,
        // unless an HDR monitor is part of the union (then compose per-monitor WGC frames).
        if (screen.DeviceName == Win32CaptureService.VirtualDesktopName)
        {
            IReadOnlyList<ScreenInfo> physical = Win32CaptureService.PhysicalScreens(_gdi.GetScreens());
            if (physical.Any(s => WinRtGraphicsCapture.NeedsHdrToneMapping(s.Handle)))
            {
                CapturedFrame composed = TryComposeVirtualDesktop(physical, screen.Bounds);
                if (composed != null)
                {
                    return composed;
                }

                Log.Warn(
                    "Snip HDR virtual-desktop compose failed; falling back to GDI (colors may be washed out)",
                    typeof(PreferWinRtCaptureService));
            }

            return _gdi.CaptureScreenPixels(screen);
        }

        if (screen.Handle != IntPtr.Zero
            && WinRtGraphicsCapture.TryCaptureMonitor(screen.Handle, captureCursor: true, out var winrt)
            && winrt != null)
        {
            return winrt;
        }

        return _gdi.CaptureScreenPixels(screen);
    }

    public CapturedFrame CaptureScreenRectPixels(int physicalX, int physicalY, int width, int height)
        => _gdi.CaptureScreenRectPixels(physicalX, physicalY, width, height);

    /// <summary>
    /// Composes the monitor union from per-monitor WGC frames. Needed when any
    /// monitor is HDR: a single GDI BitBlt of the union would clip SDR white on
    /// that monitor and produce washed-out colors.
    /// </summary>
    private static CapturedFrame TryComposeVirtualDesktop(IReadOnlyList<ScreenInfo> physical, Rect bounds)
    {
        int width = (int)Math.Round(bounds.Width);
        int height = (int)Math.Round(bounds.Height);
        if (width <= 0 || height <= 0 || physical.Count == 0)
        {
            return null;
        }

        int stride = CaptureBuffers.BgraStride(width);
        int byteCount = CaptureBuffers.BgraByteCount(width, height);
        byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        bool transferred = false;
        try
        {
            // Monitor gaps stay transparent black, matching GDI BitBlt of the union.
            Array.Clear(pixels, 0, byteCount);

            foreach (ScreenInfo monitor in physical)
            {
                if (!WinRtGraphicsCapture.TryCaptureMonitor(monitor.Handle, captureCursor: true, out CapturedFrame frame)
                    || frame == null)
                {
                    return null;
                }

                using (frame)
                using (ScrollTilePixels tile = frame.ToOwnedPixels())
                {
                    int destinationX = (int)Math.Round(monitor.Bounds.X - bounds.X);
                    int destinationY = (int)Math.Round(monitor.Bounds.Y - bounds.Y);
                    if (destinationX < 0 || destinationY < 0
                        || destinationX + tile.Width > width
                        || destinationY + tile.Height > height)
                    {
                        return null;
                    }

                    byte[] source = tile.DangerousGetArray();
                    int rowBytes = tile.Width * 4;
                    for (int y = 0; y < tile.Height; y++)
                    {
                        Array.Copy(
                            source,
                            y * tile.Stride,
                            pixels,
                            ((destinationY + y) * stride) + (destinationX * 4),
                            rowBytes);
                    }
                }
            }

            var composed = new CapturedFrame(pixels, width, height, stride, rentedFromPool: true);
            transferred = true;
            return composed;
        }
        finally
        {
            if (!transferred)
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }
    }
}
