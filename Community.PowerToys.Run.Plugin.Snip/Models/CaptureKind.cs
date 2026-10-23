// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.Models;

/// <summary>How a screenshot session is started from PowerToys Run.</summary>
public enum CaptureKind
{
    /// <summary>Full-monitor (or virtual desktop) capture; user drags a region.</summary>
    Region = 0,

    /// <summary>BitBlt of the foreground window bounds; selection pre-filled.</summary>
    ActiveWindow = 1,

    /// <summary>Same capture as region, but selection starts as the full frame.</summary>
    FullScreen = 2,
}
