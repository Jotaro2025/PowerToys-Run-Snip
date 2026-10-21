// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Core.Annotations;

/// <summary>
/// Rectangle outline. Coordinates are in the bitmap (post-selection) space.
/// </summary>
public sealed record RectAnnotation : Annotation
{
    public required Rect Rect { get; init; }

    public override Annotation ShallowCopy() => this;
}
