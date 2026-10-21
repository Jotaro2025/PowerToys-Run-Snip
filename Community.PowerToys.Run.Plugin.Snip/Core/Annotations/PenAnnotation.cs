// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.Core.Annotations;

/// <summary>
/// Free-hand pen with a polyline of points.
/// </summary>
public sealed record PenAnnotation : Annotation
{
    public required IReadOnlyList<Point> Points { get; init; }

    public override Annotation ShallowCopy() => this with { Points = Points.ToArray() };
}
