// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public sealed class DpiCoordinateMappingTests
{
    [TestMethod]
    public void DipSelectionToPhysical_ProportionalOnVirtualDesktop()
    {
        var logical = new Size(2000, 1000); // e.g. 3840x2160 @ 1.5? keep simple 2x
        var physical = new Int32Rect(0, 0, 4000, 2000);
        var sel = new Rect(100, 50, 200, 100);
        var r = DpiCoordinateMapping.DipSelectionToPhysical(sel, logical, physical);
        Assert.AreEqual(200, r.X);
        Assert.AreEqual(100, r.Y);
        Assert.AreEqual(400, r.Width);
        Assert.AreEqual(200, r.Height);
    }

    [TestMethod]
    public void DipSelectionToClientHole_UsesClientSizeNotDpiProduct()
    {
        var logical = new Size(1000, 800);
        var sel = new Rect(100, 80, 200, 160);
        var hole = DpiCoordinateMapping.DipSelectionToClientHole(sel, logical, clientWidth: 1500, clientHeight: 1200);
        Assert.AreEqual(150, hole.X);
        Assert.AreEqual(120, hole.Y);
        Assert.AreEqual(300, hole.Width);
        Assert.AreEqual(240, hole.Height);
    }

    [TestMethod]
    public void EffectiveDpiScaleAtPhysical_FallbackWhenNoMonitorApi()
    {
        double scale = DpiCoordinateMapping.EffectiveDpiScaleAtPhysical(0, 0, fallbackScale: 1.25);
        Assert.IsTrue(scale > 0.01);
    }
}
