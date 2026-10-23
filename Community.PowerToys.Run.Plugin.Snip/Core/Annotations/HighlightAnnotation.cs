// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Core.Annotations;

/// <summary>Semi-transparent filled rectangle highlight.</summary>
public sealed record HighlightAnnotation : Annotation
{
    public required Rect Rect { get; init; }

    /// <summary>Fill opacity 0–255 (default ~90).</summary>
    public byte FillAlpha { get; init; } = 90;

    public override Annotation ShallowCopy() => this;
}
