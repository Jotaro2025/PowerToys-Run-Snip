// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class ScrollCaptureControllerTests
{
    [TestMethod]
    public void IsPointInside_HalfOpenEdges()
    {
        var rect = new Int32Rect(100, 50, 20, 10);
        Assert.IsTrue(ScrollCaptureController.IsPointInside(rect, 100, 50));
        Assert.IsTrue(ScrollCaptureController.IsPointInside(rect, 119, 59));
        Assert.IsFalse(ScrollCaptureController.IsPointInside(rect, 120, 55));
        Assert.IsFalse(ScrollCaptureController.IsPointInside(rect, 110, 60));
        Assert.IsFalse(ScrollCaptureController.IsPointInside(rect, 99, 55));
    }

    [TestMethod]
    public void IsPointInside_EmptyRectIsNeverInside()
    {
        Assert.IsFalse(ScrollCaptureController.IsPointInside(new Int32Rect(0, 0, 0, 10), 0, 0));
    }

    [TestMethod]
    public void OrderTilesForStitch_DownKeepsOrder()
    {
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, ScrollCaptureController.OrderTilesForStitch(new[] { 1, 2, 3 }, scrollUp: false));
    }

    [TestMethod]
    public void OrderTilesForStitch_UpReverses()
    {
        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, ScrollCaptureController.OrderTilesForStitch(new[] { 1, 2, 3 }, scrollUp: true));
    }

    [TestMethod]
    public void ShouldCountDuplicateTowardAutoStop_RequiresSuccessfulWheel()
    {
        Assert.IsFalse(ScrollCaptureController.ShouldCountDuplicateTowardAutoStop(lastWheelSucceeded: false, successfulWheelCount: 0));
        Assert.IsFalse(ScrollCaptureController.ShouldCountDuplicateTowardAutoStop(lastWheelSucceeded: false, successfulWheelCount: 3));
        Assert.IsFalse(ScrollCaptureController.ShouldCountDuplicateTowardAutoStop(lastWheelSucceeded: true, successfulWheelCount: 0));
        Assert.IsTrue(ScrollCaptureController.ShouldCountDuplicateTowardAutoStop(lastWheelSucceeded: true, successfulWheelCount: 1));
    }

    [TestMethod]
    public void IsOwnedByOverlay_ZeroHandlesAreNotOwned()
    {
        Assert.IsFalse(Win32CaptureService.IsOwnedByOverlay(IntPtr.Zero, IntPtr.Zero));
        Assert.IsFalse(Win32CaptureService.IsOwnedByOverlay(new IntPtr(1), IntPtr.Zero));
        Assert.IsFalse(Win32CaptureService.IsOwnedByOverlay(IntPtr.Zero, new IntPtr(1)));
    }

    [TestMethod]
    public void IsOwnedByOverlay_SameHandleIsOwned()
    {
        var hwnd = new IntPtr(0x1234);
        Assert.IsTrue(Win32CaptureService.IsOwnedByOverlay(hwnd, hwnd));
    }
}
