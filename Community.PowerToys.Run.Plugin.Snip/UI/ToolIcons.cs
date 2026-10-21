// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// SVG-style path geometries used as toolbar icons. Kept inline so the plugin
/// has no external image assets.
/// </summary>
public static class ToolIcons
{
    public const string Select = "M5,3 L5,17 L9,13 L12,19 L14,17 L11,11 L17,11 Z";
    public const string Rect = "M3,3 L21,3 L21,21 L3,21 Z M5,5 L5,19 L19,19 L19,5 Z";
    public const string Ellipse = "M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4 Z M12,6 A6,6 0 1 1 12,18 A6,6 0 1 1 12,6 Z";
    public const string Arrow = "M5,12 L17,12 M13,7 L18,12 L13,17";
    public const string Pen = "M3,21 L3,17 L16,4 L20,8 L7,21 Z M14.5,5.5 L18.5,9.5";
    public const string Text = "M5,5 L19,5 M12,5 L12,19 M9,19 L15,19";
    public const string Mosaic = "M4,4 L10,4 L10,10 L4,10 Z M14,4 L20,4 L20,10 L14,10 Z M4,14 L10,14 L10,20 L4,20 Z M14,14 L20,14 L20,20 L14,20 Z";
    public const string Undo = "M9,14 L4,9 L9,4 M4,9 L13,9 C17,9 20,12 20,16 C20,19 17,21 14,21";
    public const string Ocr = "M4,8 L4,4 L8,4 M16,4 L20,4 L20,8 M20,16 L20,20 L16,20 M8,20 L4,20 L4,16 M8,9 L16,9 M12,9 L12,16";
    public const string Save = "M5,5 L17,5 L20,8 L20,19 L5,19 Z M8,5 L8,10 L16,10 L16,5 M7,14 L17,14 L17,19 L7,19 Z";
    public const string Cancel = "M6,6 L18,18 M18,6 L6,18";
    public const string Done = "M5,12 L10,17 L19,7";
}
