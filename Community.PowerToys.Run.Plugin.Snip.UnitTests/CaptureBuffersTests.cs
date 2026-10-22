// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class CaptureBuffersTests
{
    [TestMethod]
    public void BgraStrideAndByteCountMatchWidthHeight()
    {
        Assert.AreEqual(3840 * 4, CaptureBuffers.BgraStride(3840));
        Assert.AreEqual(3840 * 4 * 2160, CaptureBuffers.BgraByteCount(3840, 2160));
    }

    [TestMethod]
    public void BgraByteCountRejectsNonPositive()
    {
        _ = Assert.ThrowsException<ArgumentOutOfRangeException>(() => CaptureBuffers.BgraStride(0));
        _ = Assert.ThrowsException<ArgumentOutOfRangeException>(() => CaptureBuffers.BgraByteCount(16, 0));
        _ = Assert.ThrowsException<ArgumentOutOfRangeException>(() => CaptureBuffers.BgraByteCount(-1, 10));
    }

    [TestMethod]
    public void TopDownBgraInfoUsesNegativeHeightAnd32bpp()
    {
        var bmi = CaptureBuffers.CreateTopDownBgraInfo(1920, 1080);
        Assert.AreEqual(1920, bmi.BmiHeader.BiWidth);
        Assert.AreEqual(-1080, bmi.BmiHeader.BiHeight, "negative biHeight = top-down for WPF Bgra32");
        Assert.AreEqual(32, bmi.BmiHeader.BiBitCount);
        Assert.AreEqual(1, bmi.BmiHeader.BiPlanes);
        Assert.AreEqual(Win32.BiRgb, (int)bmi.BmiHeader.BiCompression);
        Assert.IsTrue(bmi.BmiHeader.BiSize >= 40);
    }

    [TestMethod]
    public void TopDownBgraInfoRejectsEmpty()
    {
        _ = Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => CaptureBuffers.CreateTopDownBgraInfo(0, 10));
        _ = Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => CaptureBuffers.CreateTopDownBgraInfo(10, -1));
    }
}
