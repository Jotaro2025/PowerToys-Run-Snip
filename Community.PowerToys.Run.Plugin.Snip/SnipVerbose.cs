// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip;

/// <summary>
/// Optional verbose diagnostics (default off). Scroll/stitch chatter that used to go to
/// <c>Debug.WriteLine</c> is gated here so Release stays quiet unless the user enables
/// <see cref="SnipSettings.VerboseScrollLogging"/> (respects #54: do not spam the plugin log).
/// </summary>
internal static class SnipVerbose
{
    /// <summary>When true, scroll/stitch lines go to <see cref="Log.Debug"/>.</summary>
    public static bool ScrollLogging { get; set; }

    /// <summary>Writes a scroll/stitch diagnostic when <see cref="ScrollLogging"/> is on.</summary>
    public static void Scroll(string message)
    {
        if (!ScrollLogging || string.IsNullOrEmpty(message))
        {
            return;
        }

        Log.Debug(message, typeof(SnipVerbose));
    }
}
