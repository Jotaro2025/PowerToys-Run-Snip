// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public sealed class ScrollWindowCaptureTests
{
    [TestMethod]
    public void CropBgraBuffer_CopiesSubRect()
    {
        const int srcW = 4;
        const int srcH = 3;
        int stride = CaptureBuffers.BgraStride(srcW);
        var src = new byte[stride * srcH];
        // Mark pixel (2,1) as red-ish BGRA
        int i = (1 * stride) + (2 * 4);
        src[i] = 10;
        src[i + 1] = 20;
        src[i + 2] = 30;
        src[i + 3] = 255;

        byte[] crop = ScrollWindowCapture.CropBgraBuffer(src, srcW, srcH, stride, 1, 1, 2, 1);
        Assert.AreEqual(CaptureBuffers.BgraByteCount(2, 1), crop.Length);
        // crop pixel (1,0) was src (2,1)
        Assert.AreEqual(10, crop[4]);
        Assert.AreEqual(20, crop[5]);
        Assert.AreEqual(30, crop[6]);
    }

    [TestMethod]
    public void RentCropBgraBuffer_And_ToOwnedPixels_TransfersPooledBufferWithoutExtraCopy()
    {
        const int srcW = 4;
        const int srcH = 3;
        int stride = CaptureBuffers.BgraStride(srcW);
        var src = new byte[stride * srcH];
        int i = (1 * stride) + (2 * 4);
        src[i] = 11;
        src[i + 1] = 22;
        src[i + 2] = 33;
        src[i + 3] = 255;

        byte[] rented = ScrollWindowCapture.RentCropBgraBuffer(
            src,
            srcW,
            srcH,
            stride,
            1,
            1,
            2,
            1,
            out int dstBytes);
        Assert.AreEqual(CaptureBuffers.BgraByteCount(2, 1), dstBytes);
        Assert.IsTrue(rented.Length >= dstBytes);

        using var frame = new CapturedFrame(rented, 2, 1, CaptureBuffers.BgraStride(2), rentedFromPool: true);
        using ScrollTilePixels owned = frame.ToOwnedPixels();
        Assert.AreEqual(11, owned.Pixels[4]);
        Assert.AreEqual(22, owned.Pixels[5]);
        Assert.AreEqual(33, owned.Pixels[6]);
    }

    [TestMethod]
    public void LooksBlankBgra_AllZero_True()
    {
        var zeros = new byte[64];
        Assert.IsTrue(ScrollWindowCapture.LooksBlankBgra(zeros, sampleStride: 8));
    }

    [TestMethod]
    public void LooksBlankBgra_HasColor_False()
    {
        var buf = new byte[64];
        buf[0] = 40;
        buf[1] = 40;
        buf[2] = 40;
        buf[8] = 50;
        buf[9] = 50;
        buf[10] = 50;
        buf[16] = 60;
        buf[17] = 60;
        buf[18] = 60;
        Assert.IsFalse(ScrollWindowCapture.LooksBlankBgra(buf, sampleStride: 8));
    }
}
