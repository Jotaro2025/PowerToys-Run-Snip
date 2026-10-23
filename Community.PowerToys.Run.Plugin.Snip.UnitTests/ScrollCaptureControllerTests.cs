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
        Assert.IsFalse(ScrollCaptureController.ShouldCountDuplicateTowardAutoStop(lastWheelSucceeded: true, successfulWheelCount: 1));
        Assert.IsFalse(ScrollCaptureController.ShouldCountDuplicateTowardAutoStop(
            lastWheelSucceeded: true,
            successfulWheelCount: ScrollCaptureController.MinSuccessfulWheelsBeforeDuplicateStop - 1));
        Assert.IsTrue(ScrollCaptureController.ShouldCountDuplicateTowardAutoStop(
            lastWheelSucceeded: true,
            successfulWheelCount: ScrollCaptureController.MinSuccessfulWheelsBeforeDuplicateStop));
    }

    [TestMethod]
    public void DuplicateStopThresholds_AreSoftenedForChromiumSettle()
    {
        Assert.IsTrue(ScrollCaptureController.ConsecutiveDuplicateStopThreshold >= 4);
        Assert.IsTrue(ScrollCaptureController.MinSuccessfulWheelsBeforeDuplicateStop >= 5);
        Assert.IsTrue(ScrollCaptureController.EarlyTileExtraSettleMs >= 250);
        Assert.IsTrue(Win32CaptureService.ScrollPointerSettleMs >= 80);
    }

    [TestMethod]
    public void SendInputStructSize_MatchesWin32Layout()
    {
        Assert.AreEqual(Win32CaptureService.ExpectedSendInputStructSize, Win32.InputSize);
        Assert.AreEqual(Win32.InputSize, System.Runtime.InteropServices.Marshal.SizeOf<Win32.INPUT>());
    }

    [TestMethod]
    public void CreateMouseInput_PacksWheelAndClickFlags()
    {
        var wheel = Win32CaptureService.CreateMouseInput(Win32.MouseEventFWheel, Win32CaptureService.PackSendInputWheelData(-120));
        Assert.AreEqual(Win32.InputMouse, wheel.Type);
        Assert.AreEqual(Win32.MouseEventFWheel, wheel.U.Mi.DwFlags);
        Assert.AreEqual(unchecked((uint)(-120)), wheel.U.Mi.MouseData);

        var down = Win32CaptureService.CreateMouseInput(Win32.MouseEventFLeftDown);
        Assert.AreEqual(Win32.MouseEventFLeftDown, down.U.Mi.DwFlags);
        Assert.AreEqual(0u, down.U.Mi.MouseData);
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

    [TestMethod]
    public void PackWheelWParam_PlacesDeltaInHighWord()
    {
        Assert.AreEqual(unchecked((IntPtr)(((-120) << 16) & 0xFFFFFFFF)), Win32CaptureService.PackWheelWParam(-120));
        Assert.AreEqual(unchecked((IntPtr)((120 << 16) & 0xFFFFFFFF)), Win32CaptureService.PackWheelWParam(120));
        Assert.AreEqual(unchecked((IntPtr)(((-240) << 16) & 0xFFFFFFFF)), Win32CaptureService.PackWheelWParam(-240));
    }

    [TestMethod]
    public void PackWheelLParam_PacksScreenCoords()
    {
        // x=100, y=200 → low word 100, high word 200
        Assert.AreEqual(unchecked((IntPtr)((uint)((200 << 16) | 100))), Win32CaptureService.PackWheelLParam(100, 200));
        Assert.AreEqual(unchecked((IntPtr)0u), Win32CaptureService.PackWheelLParam(0, 0));
    }

    [TestMethod]
    public void PackSendInputWheelData_PreservesSignedDeltaAsUInt()
    {
        Assert.AreEqual(unchecked((uint)(-120)), Win32CaptureService.PackSendInputWheelData(-120));
        Assert.AreEqual(120u, Win32CaptureService.PackSendInputWheelData(120));
        Assert.AreEqual(0u, Win32CaptureService.PackSendInputWheelData(0));
    }

    [TestMethod]
    public void TrySendWheelAt_ZeroDeltaReturnsFalse()
    {
        Assert.IsFalse(Win32CaptureService.TrySendWheelAt(0, 0, 0));
    }

    [TestMethod]
    public void IsOverlayCoveringPoint_ZeroOverlayNeverCovers()
    {
        var pt = new Win32.POINT { X = 0, Y = 0 };
        Assert.IsFalse(Win32CaptureService.IsOverlayCoveringPoint(pt, IntPtr.Zero));
    }

    [TestMethod]
    public void TryActivateScrollTarget_ZeroHandleReturnsFalse()
    {
        Assert.IsFalse(Win32CaptureService.TryActivateScrollTarget(IntPtr.Zero));
    }

    [TestMethod]
    public void TemporaryHideOverlayWindow_ZeroHandleIsNoOp()
    {
        using var scope = Win32CaptureService.TemporaryHideOverlayWindow(IntPtr.Zero);
        Assert.IsNotNull(scope);
    }

    [TestMethod]
    public void PostWheelNudge_ZeroTargetIsNoOp()
    {
        Win32CaptureService.PostWheelNudge(IntPtr.Zero, 10, 20, -120);
    }

    [TestMethod]
    public void ShowWindowConstants_MatchUser32()
    {
        Assert.AreEqual(0, Win32.SwHide);
        Assert.AreEqual(4, Win32.SwShowNoActivate);
        Assert.AreEqual(1u, Win32.GaParent);
        Assert.AreEqual(2u, Win32.GaRoot);
        Assert.AreEqual(unchecked((IntPtr)(-1)), Win32.HwndTopmost);
    }
}
