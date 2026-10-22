// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Captures screens as BGRA bitmaps at physical pixel resolution.
/// </summary>
public interface ICaptureService
{
    IReadOnlyList<ScreenInfo> GetScreens();

    /// <summary>
    /// GDI capture into an owned DIB / frame (safe to call off the UI thread).
    /// Dispose the frame (or call <see cref="CapturedFrame.ToWriteableBitmap"/>)
    /// to release native resources.
    /// </summary>
    CapturedFrame CaptureScreenPixels(ScreenInfo screen);

    /// <summary>
    /// Captures and builds a <see cref="WriteableBitmap"/> on the calling thread
    /// (normally the UI STA). Prefer <see cref="CaptureScreenPixels"/> +
    /// <see cref="CapturedFrame.ToWriteableBitmap"/> when offloading GDI work.
    /// </summary>
    WriteableBitmap CaptureScreen(ScreenInfo screen);

    /// <summary>
    /// GDI BitBlt of a physical-pixel rectangle (for scroll-capture tiles).
    /// Safe off the UI thread; convert with <see cref="CapturedFrame.ToWriteableBitmap"/> on STA.
    /// </summary>
    CapturedFrame CaptureScreenRectPixels(int physicalX, int physicalY, int width, int height);
}
