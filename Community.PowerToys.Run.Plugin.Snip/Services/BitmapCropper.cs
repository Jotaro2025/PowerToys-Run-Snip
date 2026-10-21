// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Crops a sub-rectangle of a <see cref="BitmapSource"/> 1:1 with the source
/// pixels. No scaling, no squishing — every destination pixel corresponds to
/// exactly one source pixel.
/// </summary>
public static class BitmapCropper
{
    public static BitmapSource Crop(BitmapSource source, Int32Rect selection)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (selection.Width <= 0 || selection.Height <= 0)
        {
            throw new ArgumentException("Selection has zero area", nameof(selection));
        }

        return new CroppedBitmap(source, selection);
    }
}
