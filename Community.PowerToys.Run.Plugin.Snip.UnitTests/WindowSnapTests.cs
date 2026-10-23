// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class WindowSnapTests
{
    [TestMethod]
    public void FindHoveredWindowPicksFrontmostCandidateInZOrder()
    {
        var candidates = new List<WindowSnapService.SnapCandidate>
        {
            new(new IntPtr(101), new Int32Rect(200, 150, 400, 300)),
            new(new IntPtr(102), new Int32Rect(100, 100, 800, 600)),
        };

        var hitFront = WindowSnapService.FindHoveredWindow(candidates, 300, 250);
        Assert.IsTrue(hitFront.HasValue);
        Assert.AreEqual(new IntPtr(101), hitFront.Value.Hwnd);

        var hitBack = WindowSnapService.FindHoveredWindow(candidates, 150, 120);
        Assert.IsTrue(hitBack.HasValue);
        Assert.AreEqual(new IntPtr(102), hitBack.Value.Hwnd);

        var miss = WindowSnapService.FindHoveredWindow(candidates, 50, 50);
        Assert.IsFalse(miss.HasValue);
    }

    [TestMethod]
    public void ScreenRectToOverlayDipClipsAndScalesCorrectly()
    {
        var captureBounds = new Int32Rect(0, 0, 3840, 2160);
        var overlayDip = new Size(1920, 1080); // 200% DPI
        var windowScreen = new Int32Rect(-100, 200, 1100, 800);

        Rect? dip = WindowSnapService.ScreenRectToOverlayDip(windowScreen, captureBounds, overlayDip);
        Assert.IsTrue(dip.HasValue);
        Assert.AreEqual(0, dip.Value.X, 1e-6);
        Assert.AreEqual(100, dip.Value.Y, 1e-6);
        Assert.AreEqual(500, dip.Value.Width, 1e-6);
        Assert.AreEqual(400, dip.Value.Height, 1e-6);
    }
}
