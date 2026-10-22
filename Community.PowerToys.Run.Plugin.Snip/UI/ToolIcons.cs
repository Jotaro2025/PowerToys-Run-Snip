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
    public const string Redo = "M15,14 L20,9 L15,4 M20,9 L11,9 C7,9 4,12 4,16 C4,19 7,21 10,21";
    public const string Ocr = "M4,8 L4,4 L8,4 M16,4 L20,4 L20,8 M20,16 L20,20 L16,20 M8,20 L4,20 L4,16 M8,9 L16,9 M12,9 L12,16";

    // Globe / swap hint for image translate.
    public const string Translate = "M4,12 A8,8 0 1 0 20,12 A8,8 0 1 0 4,12 Z M4,12 L20,12 M12,4 C9,7 9,17 12,20 M12,4 C15,7 15,17 12,20";
    public const string Save = "M5,5 L17,5 L20,8 L20,19 L5,19 Z M8,5 L8,10 L16,10 L16,5 M7,14 L17,14 L17,19 L7,19 Z";
    public const string Cancel = "M6,6 L18,18 M18,6 L6,18";
    public const string Done = "M5,12 L10,17 L19,7";

    // Pushpin (head + needle).
    public const string Pin = "M12,3 L16,7 L14,9 L18,13 L16,15 L12,11 L10,13 L8,11 L12,7 L10,5 Z M12,11 L12,21";

    // Scroll capture: stacked pages + downward arrow (from TDS ToolIcons).
    public const string ScrollCapture =
        "M7,4 L17,4 L17,17 L7,17 Z M7,7 L17,7 M7,10 L17,10 M7,13 L15,13 " +
        "M5,19 L19,19 M12,17 L12,22 M8,19 L12,22 L16,19";
}
