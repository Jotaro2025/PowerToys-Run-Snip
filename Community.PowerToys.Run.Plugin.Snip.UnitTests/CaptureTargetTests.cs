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
public class CaptureTargetTests
{
    private static ScreenInfo Monitor(string name, double x, double y, double w, double h, double dpi, bool primary)
        => new ScreenInfo(new IntPtr(1), name, new Rect(x, y, w, h), new Rect(x, y, w, h - 40), dpi, primary);

    private static ScreenInfo Virtual(double x, double y, double w, double h)
        => new ScreenInfo(IntPtr.Zero, Win32CaptureService.VirtualDesktopName, new Rect(x, y, w, h), new Rect(x, y, w, h), 1.0, true);

    [TestMethod]
    public void SingleMonitorKeepsThatMonitor()
    {
        var only = Monitor(@"\\.\DISPLAY1", 0, 0, 2560, 1440, 1.5, true);
        var screens = new List<ScreenInfo> { only, Virtual(0, 0, 2560, 1440) };

        Assert.AreSame(only, Win32CaptureService.ChooseCaptureTarget(screens));
        Assert.HasCount(1, Win32CaptureService.PhysicalScreens(screens));
    }

    [TestMethod]
    public void SeveralMonitorsUseVirtualDesktop()
    {
        var primary = Monitor(@"\\.\DISPLAY1", 0, 0, 3840, 2160, 1.5, true);
        var left = Monitor(@"\\.\DISPLAY2", -1920, 600, 1920, 1080, 1.0, false);
        var virtualDesktop = Virtual(-1920, 0, 5760, 2160);
        var screens = new List<ScreenInfo> { primary, left, virtualDesktop };

        Assert.AreSame(virtualDesktop, Win32CaptureService.ChooseCaptureTarget(screens));
        CollectionAssert.AreEqual(new[] { primary, left }, (System.Collections.ICollection)Win32CaptureService.PhysicalScreens(screens));
    }
}
