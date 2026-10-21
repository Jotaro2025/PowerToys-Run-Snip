// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows.Media;

namespace Community.PowerToys.Run.Plugin.Snip.Core.Annotations;

/// <summary>
/// Immutable annotation record. Use <see cref="ShallowCopy"/> to create modified copies.
/// </summary>
public abstract record Annotation
{
    public required AnnotationKind Kind { get; init; }

    /// <summary>
    /// Color used to stroke/fill. For Mosaic the color is ignored.
    /// </summary>
    public required Color Stroke { get; init; }

    public required double StrokeWidth { get; init; }

    public abstract Annotation ShallowCopy();
}
