// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>One background sample: image position plus its RGB color.</summary>
internal readonly struct BackgroundSample
{
    public BackgroundSample(int x, int y, byte r, byte g, byte b)
    {
        X = x;
        Y = y;
        R = r;
        G = g;
        B = b;
    }

    public int X { get; }

    public int Y { get; }

    public byte R { get; }

    public byte G { get; }

    public byte B { get; }
}
