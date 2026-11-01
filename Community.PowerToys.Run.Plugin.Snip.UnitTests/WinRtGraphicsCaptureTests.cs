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
    public void CapturedFrame_ReportsSampledLuma()
    {
        const int width = 256;
        const int height = 256;
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 100;
            pixels[i + 1] = 150;
            pixels[i + 2] = 200;
            pixels[i + 3] = 255;
        }

        using var frame = new CapturedFrame(pixels, width, height, width * 4);
        var stats = frame.GetLumaStats();

        Assert.AreEqual(150, stats.Min);
        Assert.AreEqual(150, stats.Max);
        Assert.AreEqual(150, stats.Mean, 0.01);
    }

    [TestMethod]
    public void IsLikelyBlank_FlagsAllBlackAndUniformFrames()
    {
        const int width = 256;
        const int height = 256;

        var black = new byte[width * height * 4];
        Assert.IsTrue(WinRtCaptureNative.IsLikelyBlank(black, width, height, out int min, out int max));
        Assert.AreEqual(0, min);
        Assert.AreEqual(0, max);

        var gray = new byte[width * height * 4];
        for (int i = 0; i < gray.Length; i += 4)
        {
            gray[i] = 128;
            gray[i + 1] = 128;
            gray[i + 2] = 128;
            gray[i + 3] = 255;
        }

        Assert.IsTrue(WinRtCaptureNative.IsLikelyBlank(gray, width, height, out _, out _));

        var varied = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                varied[offset] = (byte)(x * 255 / (width - 1));
                varied[offset + 1] = (byte)(y * 255 / (height - 1));
                varied[offset + 2] = 128;
                varied[offset + 3] = 255;
            }
        }

        Assert.IsFalse(WinRtCaptureNative.IsLikelyBlank(varied, width, height, out _, out _));
    }

    [TestMethod]
    public void IsLikelyBlank_IgnoresTinyCrops()
    {
        Assert.IsFalse(WinRtCaptureNative.IsLikelyBlank(new byte[32 * 32 * 4], 32, 32, out _, out _));
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
