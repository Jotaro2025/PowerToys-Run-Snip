// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class ToolbarPlacementTests
{
    private static readonly Size Toolbar = new Size(320, 40);
    private static readonly Size Viewport = new Size(1000, 800);

    [TestMethod]
    public void PrefersBelowSelectionWhenRoomExists()
    {
        var selection = new Rect(100, 100, 200, 80);
        var pos = ToolbarPlacement.Compute(selection, Toolbar, Viewport);
        Assert.AreEqual(selection.Bottom + 12, pos.Y, 0.01);
        Assert.IsTrue(pos.X >= 8);
        Assert.IsTrue(pos.X + Toolbar.Width <= Viewport.Width - 8);
    }

    [TestMethod]
    public void FlipsAboveWhenNearBottomEdge()
    {
        var selection = new Rect(100, 740, 200, 50);
        var pos = ToolbarPlacement.Compute(selection, Toolbar, Viewport);
        Assert.IsTrue(pos.Y + Toolbar.Height <= selection.Y - 1, $"expected above selection, y={pos.Y}");
        Assert.IsTrue(pos.Y >= 8);
    }

    [TestMethod]
    public void ClampsHorizontallyNearRightEdge()
    {
        var selection = new Rect(900, 100, 80, 40);
        var pos = ToolbarPlacement.Compute(selection, Toolbar, Viewport);
        Assert.AreEqual(Viewport.Width - Toolbar.Width - 8, pos.X, 0.01);
    }

    [TestMethod]
    public void StaysInsideTinyViewport()
    {
        var selection = new Rect(10, 10, 50, 50);
        var pos = ToolbarPlacement.Compute(selection, new Size(200, 40), new Size(220, 100));
        Assert.IsTrue(pos.X >= 8);
        Assert.IsTrue(pos.Y >= 8);
        Assert.IsTrue(pos.X + 200 <= 220 - 8 + 0.01);
        Assert.IsTrue(pos.Y + 40 <= 100 - 8 + 0.01);
    }

    [TestMethod]
    public void AreaOverloadMatchesViewportOverloadAtOrigin()
    {
        var selection = new Rect(420, 300, 200, 80);
        Assert.AreEqual(
            ToolbarPlacement.Compute(selection, Toolbar, Viewport),
            ToolbarPlacement.Compute(selection, Toolbar, new Rect(0, 0, Viewport.Width, Viewport.Height)));
    }

    [TestMethod]
    public void StaysInsideMonitorAreaOnMultiMonitorOverlay()
    {
        // Right monitor (1000..1800, 0..600) is shorter than the overlay (height 1000):
        // a selection near its bottom must not push the toolbar into the gap below it.
        var rightMonitor = new Rect(1000, 0, 800, 600);
        var selection = new Rect(1700, 540, 90, 40);

        var pos = ToolbarPlacement.Compute(selection, Toolbar, rightMonitor);

        Assert.IsTrue(pos.X >= rightMonitor.Left + 8, $"x={pos.X}");
        Assert.IsTrue(pos.X + Toolbar.Width <= rightMonitor.Right - 8, $"x={pos.X}");
        Assert.IsTrue(pos.Y + Toolbar.Height <= selection.Y, $"expected above selection, y={pos.Y}");
        Assert.IsTrue(pos.Y >= rightMonitor.Top + 8);
    }

    [TestMethod]
    public void MonitorAreaWithNegativeOriginClampsLeft()
    {
        var leftMonitor = new Rect(-1200, 0, 1200, 900);
        var selection = new Rect(-1195, 100, 40, 40);

        var pos = ToolbarPlacement.Compute(selection, Toolbar, leftMonitor);

        Assert.AreEqual(-1200 + 8, pos.X, 0.01);
        Assert.AreEqual(selection.Bottom + 12, pos.Y, 0.01);
    }
}
