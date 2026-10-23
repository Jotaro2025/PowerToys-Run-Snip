// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows.Media;
using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Community.PowerToys.Run.Plugin.Snip.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class PinImageTests
{
    [TestMethod]
    public void EditOutcomeIncludesPinned()
    {
        Assert.AreEqual("Pinned", EditOutcome.Pinned.ToString());
    }

    [TestMethod]
    public void ToolIconsPinGeometryIsDefined()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(ToolIcons.Pin));
        Assert.IsTrue(ToolIcons.Pin.Contains('M', System.StringComparison.Ordinal));
    }

    [TestMethod]
    public void ApplyZoomClampsAndSteps()
    {
        Assert.AreEqual(1.1, PinImageWindow.ApplyZoom(1.0, 120), 1e-9);
        Assert.AreEqual(1.0 / 1.1, PinImageWindow.ApplyZoom(1.0, -120), 1e-9);
        Assert.AreEqual(PinImageWindow.MinZoom, PinImageWindow.ApplyZoom(PinImageWindow.MinZoom, -120), 1e-9);
        Assert.AreEqual(PinImageWindow.MaxZoom, PinImageWindow.ApplyZoom(PinImageWindow.MaxZoom, 120), 1e-9);
    }

    [TestMethod]
    public void ApplyOpacityClampsAndSteps()
    {
        Assert.AreEqual(0.85, PinImageWindow.ApplyOpacity(0.8, 120), 1e-9);
        Assert.AreEqual(0.75, PinImageWindow.ApplyOpacity(0.8, -120), 1e-9);
        Assert.AreEqual(PinImageWindow.MinOpacity, PinImageWindow.ApplyOpacity(PinImageWindow.MinOpacity, -120), 1e-9);
        Assert.AreEqual(PinImageWindow.MaxOpacity, PinImageWindow.ApplyOpacity(PinImageWindow.MaxOpacity, 120), 1e-9);
    }

    [TestMethod]
    public void ComputeInitialPinZoomFitsTallScrolledImageWithinWorkArea()
    {
        // Small image fits 1:1
        Assert.AreEqual(1.0, BitmapPreview.ComputeInitialPinZoom(400, 300, 96, 96, 1920, 1080), 1e-9);

        // Tall 1200x4500 scrolled image on 1920x1000 work area scales down to 900/4500 = 0.2
        double zoom = BitmapPreview.ComputeInitialPinZoom(1200, 4500, 96, 96, 1920, 1000, PinImageWindow.MinZoom);
        Assert.AreEqual(0.2, zoom, 1e-6);
    }

    [TestMethod]
    public void SelectScalingModeUsesHighQualityBelow2xAndNearestNeighborAt2xOrAbove()
    {
        Assert.AreEqual(BitmapScalingMode.HighQuality, PinImageWindow.SelectScalingMode(0.5));
        Assert.AreEqual(BitmapScalingMode.HighQuality, PinImageWindow.SelectScalingMode(1.0));
        Assert.AreEqual(BitmapScalingMode.HighQuality, PinImageWindow.SelectScalingMode(1.9));
        Assert.AreEqual(BitmapScalingMode.NearestNeighbor, PinImageWindow.SelectScalingMode(2.0));
        Assert.AreEqual(BitmapScalingMode.NearestNeighbor, PinImageWindow.SelectScalingMode(4.0));
    }

    [TestMethod]
    public void ComputeClickThroughExStyleTogglesLayeredAndTransparentBits()
    {
        long baseStyle = 0x00000100L;
        long enabled = PinImageWindow.ComputeClickThroughExStyle(baseStyle, baseStyle, enable: true);
        Assert.AreNotEqual(0L, enabled & Win32.WsExLayered);
        Assert.AreNotEqual(0L, enabled & Win32.WsExTransparent);

        long disabled = PinImageWindow.ComputeClickThroughExStyle(baseStyle, enabled, enable: false);
        Assert.AreEqual(baseStyle, disabled);
    }
}
