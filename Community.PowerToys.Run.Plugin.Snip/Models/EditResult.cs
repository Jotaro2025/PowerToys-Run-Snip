// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Core.Annotations;

namespace Community.PowerToys.Run.Plugin.Snip.Models;

/// <summary>
/// Result of one screenshot session. <see cref="PngBytes"/> is <c>null</c> when cancelled.
/// </summary>
public sealed record EditResult
{
    public required EditOutcome Outcome { get; init; }

    /// <summary>Selection rectangle in the captured bitmap's pixel space. <c>null</c> when cancelled.</summary>
    public Rect? Selection { get; init; }

    /// <summary>Annotations drawn on the image. Empty when cancelled.</summary>
    public IReadOnlyList<Annotation> Annotations { get; init; } = [];

    /// <summary>Final rendered bitmap (size = selection size). <c>null</c> when cancelled.</summary>
    public BitmapSource Result { get; init; }

    /// <summary>PNG bytes of <see cref="Result"/>. <c>null</c> when cancelled.</summary>
    public byte[] PngBytes { get; init; }

    /// <summary>File path the user saved to (only when <see cref="EditOutcome.Saved"/>).</summary>
    public string SavedPath { get; init; }
}
