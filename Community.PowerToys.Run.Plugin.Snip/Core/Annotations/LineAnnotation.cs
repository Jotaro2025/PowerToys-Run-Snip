// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Core.Annotations;

/// <summary>Straight line from <see cref="Start"/> to <see cref="End"/> (no arrowhead).</summary>
public sealed record LineAnnotation : Annotation
{
    public required Point Start { get; init; }

    public required Point End { get; init; }

    public override Annotation ShallowCopy() => this;
}
