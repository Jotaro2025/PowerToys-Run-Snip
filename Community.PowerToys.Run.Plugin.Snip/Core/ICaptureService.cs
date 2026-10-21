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

    WriteableBitmap CaptureScreen(ScreenInfo screen);
}
