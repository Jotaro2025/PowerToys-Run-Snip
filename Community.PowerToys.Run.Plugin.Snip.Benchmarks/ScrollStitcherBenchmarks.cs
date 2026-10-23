// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Community.PowerToys.Run.Plugin.Snip.Core;

namespace Community.PowerToys.Run.Plugin.Snip.Benchmarks;

/// <summary>
/// Synthetic tiled content: luminance varies with the document row so a scrolled
/// pair has an unambiguous best-dh (same trick as ScrollStitcherTests).
/// </summary>
internal static class TileFactory
{
    public static (ScrollTilePixels Prev, ScrollTilePixels Next) CreateScrolledPair(int width, int height, int dh)
    {
        var prev = new byte[width * 4 * height];
        var next = new byte[width * 4 * height];
        int stride = width * 4;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * stride) + (x * 4);
                Fill(prev, o, x, y);
                int ny = y + dh;
                Fill(next, o, x, ny < height ? ny : (height - 1) - ((ny - height) & 0xFF));
            }
        }

        void Fill(byte[] buf, int o, int x, int y)
        {
            byte v = (byte)((y * 29 + x * 11) & 0xFF);
            buf[o] = v;
            buf[o + 1] = (byte)((y * 43 ^ (x * 17)) & 0xFF);
            buf[o + 2] = (byte)(((y * 19) + (x * 37)) & 0xFF);
            buf[o + 3] = 255;
        }

        return (new ScrollTilePixels(prev, width, height, stride), new ScrollTilePixels(next, width, height, stride));
    }
}

[MemoryDiagnoser]
public class ScrollStitcherBenchmarks
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int Dh = 48;

    private ScrollTilePixels _prev = null!;
    private ScrollTilePixels _next = null!;

    [GlobalSetup]
    public void Setup()
    {
        (_prev, _next) = TileFactory.CreateScrolledPair(Width, Height, Dh);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _prev.Dispose();
        _next.Dispose();
    }

    /// <summary>Per-pair alignment as run during capture (hint window + SAD refine).</summary>
    [Benchmark]
    public void AlignTiles_WithHint() => ScrollStitcher.AlignTiles(_prev, _next, Dh);

    /// <summary>Cold-start path for tile #1 (FFT + full dh scan).</summary>
    [Benchmark]
    public void AlignTiles_FftColdStart() => ScrollStitcher.AlignTiles(_prev, _next, 0);

    [Benchmark]
    public void EstimateContributedRows() => ScrollStitcher.EstimateContributedRows(_prev, _next, Dh);
}

[MemoryDiagnoser]
public class ScrollPhaseCorrelatorBenchmarks
{
    private const int Width = 1280;
    private const int Height = 720;

    private ScrollTilePixels _prev = null!;
    private ScrollTilePixels _next = null!;

    [GlobalSetup]
    public unsafe void Setup()
    {
        (_prev, _next) = TileFactory.CreateScrolledPair(Width, Height, 40);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _prev.Dispose();
        _next.Dispose();
    }

    [Benchmark]
    public unsafe void EstimateVerticalShift()
    {
        fixed (byte* pb = _prev.DangerousGetArray())
        fixed (byte* nb = _next.DangerousGetArray())
        {
            _ = ScrollPhaseCorrelator.EstimateVerticalShift(
                pb, _prev.Stride, _prev.Height,
                nb, _next.Stride, _next.Height,
                _prev.Width, minDh: 4, maxDh: _prev.Height - 8, scrollHintPx: 0);
        }
    }
}

[MemoryDiagnoser]
public class ComposeStitchBenchmarks
{
    private const int Width = 1280;
    private const int TileHeight = 720;
    private const int TileCount = 8;

    private List<ScrollTilePixels> _tiles = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tiles = new List<ScrollTilePixels>(TileCount);
        for (int i = 0; i < TileCount; i++)
        {
            _tiles.Add(TileFactory.CreateScrolledPair(Width, TileHeight, dh: 48 * (i + 1)).Next);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var t in _tiles)
        {
            t.Dispose();
        }
    }

    /// <summary>
    /// Stitch re-runs alignment per pair (no precomputed cache) — the terminal
    /// compose path after auto-scroll capture finishes.
    /// </summary>
    [Benchmark]
    public void StitchTiles()
    {
        using var result = ScrollStitcher.Stitch(_tiles);
    }
}
