// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

namespace Community.PowerToys.Run.Plugin.Snip;

/// <summary>
/// Plugin options, persisted by PowerToys Run through the additional options
/// of <see cref="Wox.Plugin.ISettingProvider"/>.
/// </summary>
public sealed class SnipSettings
{
    /// <summary>Directory auto-saved screenshots go to. Empty = Pictures\Snip.</summary>
    public string SaveDirectory { get; set; } = string.Empty;

    /// <summary>Copy the screenshot to the clipboard after confirming.</summary>
    public bool CopyAfterCapture { get; set; } = true;

    /// <summary>Save the screenshot to <see cref="SaveDirectory"/> after confirming.</summary>
    public bool AutoSaveAfterCapture { get; set; }

    /// <summary>Delay before the capture starts, giving PowerToys Run time to hide.</summary>
    public int CaptureDelayMs { get; set; } = 150;

    /// <summary>Fallback directory when <see cref="SaveDirectory"/> is not set.</summary>
    public static string DefaultSaveFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Snip");
}
