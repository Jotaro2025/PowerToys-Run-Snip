// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Core.Annotations;

/// <summary>Numbered step marker (filled circle + digit) at <see cref="Center"/>.</summary>
public sealed record StepAnnotation : Annotation
{
    public required Point Center { get; init; }

    public required int Number { get; init; }

    public double Radius { get; init; } = 14;

    public override Annotation ShallowCopy() => this;
}
