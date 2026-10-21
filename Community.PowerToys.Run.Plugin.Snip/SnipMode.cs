// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

namespace Community.PowerToys.Run.Plugin.Snip;

/// <summary>
/// Which screenshot action the user picked.
/// </summary>
public enum SnipMode
{
    /// <summary>Open the fullscreen editor on a user-selected region.</summary>
    Region,

    /// <summary>Capture the screen under the cursor straight to the clipboard.</summary>
    FullScreenClipboard,

    /// <summary>Capture the screen under the cursor and save it as PNG.</summary>
    FullScreenSave,
}
