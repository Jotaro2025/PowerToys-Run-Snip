// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Core.Annotations;

/// <summary>
/// Pixelated mosaic that hides the underlying area.
/// </summary>
public sealed record MosaicAnnotation : Annotation
{
    public required Rect Rect { get; init; }

    public required int BlockSize { get; init; }

    public override Annotation ShallowCopy() => this;
}
