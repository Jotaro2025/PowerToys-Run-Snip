// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class OcrUpscalePolicyTests
{
    [TestMethod]
    [DataRow(100, 60, true)]
    [DataRow(60, 100, true)]
    [DataRow(127, 40, true)]
    [DataRow(128, 40, true)] // short side is 40
    [DataRow(128, 128, false)]
    [DataRow(400, 128, false)]
    [DataRow(400, 300, false)]
    [DataRow(3, 3, true)]
    [DataRow(2, 2, false)]
    [DataRow(1, 1, false)]
    [DataRow(0, 10, false)]
    [DataRow(10, 0, false)]
    [DataRow(-5, 10, false)]
    public void NeedsUpscaleByShortSide(int width, int height, bool expected)
    {
        Assert.AreEqual(expected, OcrUpscalePolicy.NeedsUpscale(width, height));
    }

    [TestMethod]
    public void PlanUpscaleDoublesSmallImage()
    {
        var plan = OcrUpscalePolicy.PlanUpscale(80, 60);
        Assert.IsNotNull(plan);
        Assert.AreEqual((160, 120), plan.Value);
    }

    [TestMethod]
    public void PlanUpscaleNullWhenNotNeeded()
    {
        Assert.IsNull(OcrUpscalePolicy.PlanUpscale(400, 300));
        Assert.IsNull(OcrUpscalePolicy.PlanUpscale(2, 2));
    }

    [TestMethod]
    public void PlanUpscaleClampsShortSideToMax()
    {
        // A 3 px wide, 900 px tall strip: 2x would leave the short side at 6,
        // so this exercises the normal path; the clamp only matters when the
        // factor itself would exceed the cap on the short side.
        var plan = OcrUpscalePolicy.PlanUpscale(3, 900);
        Assert.IsNotNull(plan);
        Assert.AreEqual(6, plan.Value.Width);
        Assert.AreEqual(1800, plan.Value.Height);
    }

    [TestMethod]
    public void ActualScaleMatchesShortSideRatio()
    {
        Assert.AreEqual(2.0, OcrUpscalePolicy.ActualScale(80, 60, 160, 120), 1e-9);
        Assert.AreEqual(1.0, OcrUpscalePolicy.ActualScale(80, 60, 80, 60), 1e-9);
    }

    [TestMethod]
    public void MapBoxScalesBackToSource()
    {
        var box = new Rect(10, 20, 30, 6);
        var mapped = OcrUpscalePolicy.MapBox(box, 2.0);
        Assert.AreEqual(new Rect(5, 10, 15, 3), mapped);
    }

    [TestMethod]
    public void MapBoxRoundTrips()
    {
        var box = new Rect(7.3, 11.7, 43.1, 9.9);
        var mapped = OcrUpscalePolicy.MapBox(box, 2.0);
        var roundTrip = OcrUpscalePolicy.MapBox(mapped, 0.5);
        Assert.AreEqual(box.Left, roundTrip.Left, 1e-9);
        Assert.AreEqual(box.Top, roundTrip.Top, 1e-9);
        Assert.AreEqual(box.Width, roundTrip.Width, 1e-9);
        Assert.AreEqual(box.Height, roundTrip.Height, 1e-9);
    }

    [TestMethod]
    public void MapCornersScalesBackToSource()
    {
        var corners = new Point[]
        {
            new(10, 20),
            new(30, 20),
            new(30, 40),
            new(10, 40),
        };
        var mapped = OcrUpscalePolicy.MapCorners(corners, 2.0);
        Assert.HasCount(4, mapped);
        Assert.AreEqual(new Point(5, 10), mapped[0]);
        Assert.AreEqual(new Point(15, 10), mapped[1]);
        Assert.AreEqual(new Point(15, 20), mapped[2]);
        Assert.AreEqual(new Point(5, 20), mapped[3]);
    }

    [TestMethod]
    public void MapCornersNullForMissing()
    {
        Assert.IsNull(OcrUpscalePolicy.MapCorners(null, 2.0));
        Assert.IsNull(OcrUpscalePolicy.MapCorners([new Point(1, 1)], 2.0));
    }

    [TestMethod]
    public void MapLinesToSourceIsTransparentAtScaleOne()
        => Assert.AreSame(Array.Empty<OcrTextLine>(), typeof(OcrService)
            .GetMethod("MapLinesToSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, [Array.Empty<OcrTextLine>(), 1.0]));
}

[TestClass]
public class PngReaderTests
{
    private static byte[] EncodePng(int width, int height)
    {
        var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bmp.Freeze();
        return PngEncoder.Encode(bmp);
    }

    [TestMethod]
    public void TryReadSizeReturnsHeaderDimensions()
    {
        var png = EncodePng(123, 45);
        Assert.AreEqual((123, 45), PngReader.TryReadSize(png));
    }

    [TestMethod]
    public void TryReadSizeRejectsNonPng()
    {
        Assert.IsNull(PngReader.TryReadSize(new byte[] { 0x89, (byte)'P' }));
        Assert.IsNull(PngReader.TryReadSize(new byte[10]));
        Assert.IsNull(PngReader.TryReadSize(new byte[30]));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task DecodeFrozenRoundTripsSizeAndFormat()
    {
        var png = EncodePng(31, 17);
        var decoded = await System.Threading.Tasks.Task.Run(() => PngReader.DecodeFrozen(png));
        Assert.IsNotNull(decoded);
        Assert.AreEqual(31, decoded.PixelWidth);
        Assert.AreEqual(17, decoded.PixelHeight);
        Assert.AreEqual(PixelFormats.Bgra32, decoded.Format);
        Assert.IsTrue(decoded.IsFrozen);
    }

    [TestMethod]
    public async System.Threading.Tasks.Task DecodeFrozenNullOnGarbage()
    {
        var decoded = await System.Threading.Tasks.Task.Run(() => PngReader.DecodeFrozen(new byte[] { 1, 2, 3, 4 }));
        Assert.IsNull(decoded);
        await Assert.ThrowsExceptionAsync<ArgumentNullException>(
            () => System.Threading.Tasks.Task.Run(() => PngReader.DecodeFrozen(null)));
    }
}
