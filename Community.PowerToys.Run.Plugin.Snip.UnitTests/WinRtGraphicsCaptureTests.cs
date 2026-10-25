// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public sealed class WinRtGraphicsCaptureTests
{
    [TestMethod]
    public void CaptureFactory_OnWindows_ReturnsPreferWinRt()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only");
        }

        var svc = CaptureFactory.Create();
        Assert.IsInstanceOfType<PreferWinRtCaptureService>(svc);
    }

    [TestMethod]
    public void IsSupported_DoesNotThrow()
    {
        _ = WinRtGraphicsCapture.IsSupported;
    }

    [TestMethod]
    public void TryCaptureMonitor_ZeroHandle_ReturnsFalse()
    {
        Assert.IsFalse(WinRtGraphicsCapture.TryCaptureMonitor(IntPtr.Zero, true, out var frame));
        Assert.IsNull(frame);
    }

    [TestMethod]
    public void TryCaptureWindow_ZeroHandle_ReturnsFalse()
    {
        Assert.IsFalse(WinRtGraphicsCapture.TryCaptureWindow(IntPtr.Zero, true, out var frame));
        Assert.IsNull(frame);
    }

    [TestMethod]
    public void PreferWinRt_RectPath_DelegatesToGdiShape()
    {
        // Construction must not throw; rect path is GDI (no WinRT) — exercised by existing scroll tests.
        var svc = new PreferWinRtCaptureService();
        Assert.IsNotNull(svc.Gdi);
    }

    [TestMethod]
    public void CaptureGate_IsAvailableWhenIdle()
    {
        Assert.IsTrue(WinRtGraphicsCapture.CaptureGateAvailableForTests);
    }

    [TestMethod]
    public void PrewarmDevice_DoesNotThrow_AndKeepsGateFree()
    {
        // Unsupported platforms no-op; supported ones may create the device.
        // Either way the call must be safe off the STA and leave the gate free.
        WinRtGraphicsCapture.PrewarmDevice();
        Assert.IsTrue(WinRtGraphicsCapture.CaptureGateAvailableForTests);
    }
}
