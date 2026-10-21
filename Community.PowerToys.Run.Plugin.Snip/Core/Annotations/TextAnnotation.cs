// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Core.Annotations;

/// <summary>
/// Text annotation anchored at <see cref="Origin"/>. The text is rendered in a
/// single line using the system default font.
/// </summary>
public sealed record TextAnnotation : Annotation
{
    public required string Text { get; init; }

    public required Point Origin { get; init; }

    public required double FontSize { get; init; }

    public override Annotation ShallowCopy() => this;
}
