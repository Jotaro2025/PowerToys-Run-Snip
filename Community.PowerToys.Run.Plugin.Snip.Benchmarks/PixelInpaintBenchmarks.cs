// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using BenchmarkDotNet.Attributes;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.Services;

namespace Community.PowerToys.Run.Plugin.Snip.Benchmarks;

[MemoryDiagnoser]
public class LightInpaintBenchmarks
{
    private const int Width = 640;
    private const int Height = 480;
    private const int Stride = Width * 4;

    private byte[] _pixels = null!;

    [Params(32, 96, 160)]
    public int BoxSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _pixels = new byte[Stride * Height];
        var rng = new Random(7);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Stride) + (x * 4);
                byte v = (byte)((x * 255) / (Width - 1));
                _pixels[i] = v;
                _pixels[i + 1] = v;
                _pixels[i + 2] = v;
                _pixels[i + 3] = 255;
            }
        }

        // "Text" residue inside the hole so inpaint has real structure to erase.
        int x0 = (Width - BoxSize) / 2;
        int y0 = (Height - BoxSize) / 2;
        for (int y = y0; y < y0 + BoxSize; y++)
        {
            for (int x = x0; x < x0 + BoxSize; x++)
            {
                int i = (y * Stride) + (x * 4);
                byte v = ((x + y) & 1) == 0 ? (byte)0 : (byte)255;
                _pixels[i] = v;
                _pixels[i + 1] = v;
                _pixels[i + 2] = v;
            }
        }
    }

    [Benchmark]
    public bool TryInpaintRect() => LightInpaint.TryInpaintRect(
        _pixels, Stride, Width, Height,
        (Width - BoxSize) / 2, (Height - BoxSize) / 2,
        (Width + BoxSize) / 2, (Height + BoxSize) / 2);
}

[MemoryDiagnoser]
public class PixelMagnifierBenchmarks
{
    private byte[] _src = null!;
    private byte[] _dest = null!;

    [Params(1920, 2560, 3840)]
    public int SourceWidth { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int height = 2160;
        int stride = SourceWidth * 4;
        _src = new byte[stride * height];
        var rng = new Random(11);
        for (int i = 0; i < _src.Length; i += 4)
        {
            _src[i] = (byte)rng.Next(256);
            _src[i + 1] = (byte)rng.Next(256);
            _src[i + 2] = (byte)rng.Next(256);
            _src[i + 3] = 255;
        }

        _dest = new byte[PixelMagnifier.OutputPixelSize * PixelMagnifier.OutputPixelSize * 4];
    }

    [Benchmark]
    public void FillZoomedGrid() => PixelMagnifier.FillZoomedGrid(
        _src, SourceWidth * 4, SourceWidth, 2160,
        SourceWidth / 2, 1080,
        _dest, PixelMagnifier.OutputPixelSize * 4);
}
