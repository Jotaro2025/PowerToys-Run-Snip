// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class ScrollStitcherTests
{
    private const int Width = 64;
    private const int Height = 48;
    private const int Stride = Width * 4;

    [TestMethod]
    public void IsNearlyIdentical_TrueForSameBuffer()
    {
        using var a = SolidTile(40, 80, 120);
        using var b = SolidTile(40, 80, 120);
        Assert.IsTrue(ScrollStitcher.IsNearlyIdentical(a, b));
    }

    [TestMethod]
    public void IsNearlyIdentical_FalseWhenContentDiffers()
    {
        using var a = SolidTile(40, 80, 120);
        using var b = SolidTile(200, 10, 10);
        Assert.IsFalse(ScrollStitcher.IsNearlyIdentical(a, b));
    }

    [TestMethod]
    public unsafe void AlignVerticalTiles_DetectsFullDuplicate()
    {
        using var a = SolidTile(10, 20, 30);
        using var b = SolidTile(10, 20, 30);
        fixed (byte* pa = a.DangerousGetArray())
        fixed (byte* pb = b.DangerousGetArray())
        {
            var (skip, mode, relation) = ScrollStitcher.AlignVerticalTilesFromPointers(
                pa, Stride, Height, pb, Stride, Height, Width, scrollHintPx: 0);
            Assert.AreEqual(ScrollStitcher.TileRelation.FullDuplicate, relation);
            Assert.AreEqual(Height, skip);
            Assert.AreEqual("raw-dup", mode);
        }
    }

    [TestMethod]
    public unsafe void AlignVerticalTiles_PartialOverlapWithKnownShift()
    {
        // Tile B is A scrolled down by 12px (top 12 rows of B are new; rest matches A[12..]).
        const int dh = 12;
        using var a = GradientVerticalTile();
        using var b = ShiftedDown(a, dh);

        fixed (byte* pa = a.DangerousGetArray())
        fixed (byte* pb = b.DangerousGetArray())
        {
            var (skip, mode, relation) = ScrollStitcher.AlignVerticalTilesFromPointers(
                pa, Stride, Height, pb, Stride, Height, Width, scrollHintPx: dh);
            Assert.AreEqual(ScrollStitcher.TileRelation.PartialOverlap, relation);
            Assert.AreEqual(Height - dh, skip, $"mode={mode}");
        }
    }

    [TestMethod]
    public void ResolveMaxStitchedHeight_NarrowUsesPixelCap()
    {
        Assert.AreEqual(ScrollStitcher.MaxStitchedHeightPx, ScrollStitcher.ResolveMaxStitchedHeight(Width));
    }

    [TestMethod]
    public void ResolveMaxStitchedHeight_WideUsesByteCap()
    {
        const int wide = 8192;
        long expected = ScrollStitcher.MaxStitchedBytes / (wide * 4L);
        Assert.AreEqual((int)Math.Min(ScrollStitcher.MaxStitchedHeightPx, expected), ScrollStitcher.ResolveMaxStitchedHeight(wide));
        Assert.IsTrue(ScrollStitcher.ResolveMaxStitchedHeight(wide) < ScrollStitcher.MaxStitchedHeightPx);
    }

    [TestMethod]
    public void EstimateContributedRows_MatchesShiftAndZeroForDuplicate()
    {
        const int dh = 12;
        using var a = GradientVerticalTile();
        using var b = ShiftedDown(a, dh);
        Assert.AreEqual(dh, ScrollStitcher.EstimateContributedRows(a, b, dh));

        using var c = SolidTile(1, 2, 3);
        using var d = SolidTile(1, 2, 3);
        Assert.AreEqual(0, ScrollStitcher.EstimateContributedRows(c, d));
    }

    [TestMethod]
    public void Stitch_TwoShiftedTiles_ProducesExpectedHeight()
    {
        const int dh = 16;
        using var a = GradientVerticalTile();
        using var b = ShiftedDown(a, dh);
        var tiles = new List<ScrollTilePixels> { a, b };
        using var result = ScrollStitcher.Stitch(tiles);
        Assert.AreEqual(Height + dh, result.TotalHeight);
        Assert.AreEqual(Width, result.Width);
        Assert.AreEqual(2, result.Offsets.Count);
        Assert.AreEqual(Height, result.Offsets[0].RowsContributed);
        Assert.AreEqual(dh, result.Offsets[1].RowsContributed);
    }

    [TestMethod]
    public void Stitch_DuplicateMiddleTile_ContributesZero()
    {
        using var a = GradientVerticalTile();
        using var dup = GradientVerticalTile();
        using var c = ShiftedDown(a, 10);
        // a, identical copy, then shifted — middle should be FullDuplicate at stitch time
        var tiles = new List<ScrollTilePixels> { a, dup, c };
        using var result = ScrollStitcher.Stitch(tiles);
        Assert.AreEqual(0, result.Offsets[1].RowsContributed);
        Assert.IsTrue(result.TotalHeight >= Height + 10);
    }

    private static ScrollTilePixels SolidTile(byte b, byte g, byte r)
    {
        var buf = new byte[Stride * Height];
        for (int i = 0; i < buf.Length; i += 4)
        {
            buf[i] = b;
            buf[i + 1] = g;
            buf[i + 2] = r;
            buf[i + 3] = 255;
        }

        return new ScrollTilePixels(buf, Width, Height, Stride);
    }

    private static ScrollTilePixels GradientVerticalTile()
    {
        var buf = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
        {
            byte v = (byte)((y * 255) / Math.Max(1, Height - 1));
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Stride) + (x * 4);
                buf[o] = v;
                buf[o + 1] = (byte)(255 - v);
                buf[o + 2] = (byte)((x * 3) % 256);
                buf[o + 3] = 255;
            }
        }

        return new ScrollTilePixels(buf, Width, Height, Stride);
    }

    private static ScrollTilePixels ShiftedDown(ScrollTilePixels src, int dh)
    {
        var buf = new byte[Stride * Height];
        var srcArr = src.DangerousGetArray();

        // Top dh rows: unique noise so they do not match src top.
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Stride) + (x * 4);
                buf[o] = (byte)(y + 7);
                buf[o + 1] = (byte)(x + 11);
                buf[o + 2] = (byte)(y * x);
                buf[o + 3] = 255;
            }
        }

        // Remaining rows = src rows [dh .. Height) placed at [0..] of logical scroll:
        // After scroll down by dh, new tile shows new content on top and old content below.
        // So b[dh..] should equal a[0 .. Height-dh].
        int copyRows = Height - dh;
        for (int y = 0; y < copyRows; y++)
        {
            srcArr.AsSpan(y * Stride, Stride).CopyTo(buf.AsSpan((y + dh) * Stride, Stride));
        }

        return new ScrollTilePixels(buf, Width, Height, Stride);
    }
}
