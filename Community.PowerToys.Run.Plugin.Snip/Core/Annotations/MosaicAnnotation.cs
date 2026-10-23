// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Core.Annotations;

/// <summary>Visual redaction mode for <see cref="MosaicAnnotation"/>.</summary>
public enum MosaicEffectKind
{
    /// <summary>Classic pixelated block average.</summary>
    Pixelate = 0,

    /// <summary>Smooth separable box/Gaussian blur.</summary>
    GaussianBlur = 1,

    /// <summary>Solid color fill redaction using the annotation stroke color.</summary>
    SolidRedact = 2,
}

/// <summary>
/// Redaction region (pixelated mosaic, Gaussian blur, or solid color fill) that hides the underlying area.
/// </summary>
public sealed record MosaicAnnotation : Annotation
{
    public required Rect Rect { get; init; }

    public required int BlockSize { get; init; }

    public MosaicEffectKind Effect { get; init; } = MosaicEffectKind.Pixelate;

    public override Annotation ShallowCopy() => this;
}
