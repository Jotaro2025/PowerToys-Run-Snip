// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows.Media;

namespace Community.PowerToys.Run.Plugin.Snip.Models;

/// <summary>
/// Describes how the screenshot editor should open.
/// </summary>
public sealed class EditRequest
{
    /// <summary>Default stroke color for tools. <c>null</c> = use UI default (red).</summary>
    public Color? DefaultStroke { get; init; } = Color.FromRgb(238, 32, 77);

    /// <summary>Default stroke width in DIPs.</summary>
    public double DefaultStrokeWidth { get; init; } = 3;

    /// <summary>If true, the toolbar offers a "Save" button in addition to "Done".</summary>
    public bool ShowSaveButton { get; init; } = true;

    /// <summary>Initial tool selected on toolbar (Select by default).</summary>
    public string InitialTool { get; init; } = ToolIds.Select;
}
