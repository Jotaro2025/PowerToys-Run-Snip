// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

/// <summary>Pixel-level tests for premultiplied blend + edge clipping in <see cref="ImageTranslateRenderer"/>.</summary>
[TestClass]
public class ImageTranslateRendererBlendTests
{
    [TestMethod]
    public void Blend_TransparentSourceLeavesDestination()
    {
        var src = new byte[4 * 4];
        var dst = Fill(2, 2, 10, 20, 30);
        var before = (byte[])dst.Clone();

        ImageTranslateRenderer.BlendPremultipliedOver(src, 8, 2, 2, dst, 8, 2, 2, 0, 0);

        CollectionAssert.AreEqual(before, dst);
    }

    [TestMethod]
    public void Blend_OpaqueSourceReplacesDestination()
    {
        var src = Fill(1, 1, 200, 100, 50, a: 255);
        var dst = Fill(1, 1, 10, 20, 30);

        var dirty = ImageTranslateRenderer.BlendPremultipliedOver(src, 4, 1, 1, dst, 4, 1, 1, 0, 0);

        CollectionAssert.AreEqual(new byte[] { 200, 100, 50, 255 }, dst);
        Assert.AreEqual(new Int32Rect(0, 0, 1, 1), dirty);
    }

    [TestMethod]
    public void Blend_HalfAlphaPremultipliedEdge()
    {
        // Premultiplied white at ~50% alpha over black → ~mid gray, alpha forced opaque.
        var src = Fill(1, 1, 128, 128, 128, a: 128);
        var dst = Fill(1, 1, 0, 0, 0);

        ImageTranslateRenderer.BlendPremultipliedOver(src, 4, 1, 1, dst, 4, 1, 1, 0, 0);

        Assert.AreEqual(128, dst[0]);
        Assert.AreEqual(128, dst[1]);
        Assert.AreEqual(128, dst[2]);
        Assert.AreEqual(255, dst[3]);
    }

    [TestMethod]
    public void Blend_HalfAlphaOverWhite_MatchesSourceOverFormula()
    {
        // Premultiplied black at a=64 over white: dst = 0 + 255 * (255-64)/255 = 191.
        var src = Fill(1, 1, 0, 0, 0, a: 64);
        var dst = Fill(1, 1, 255, 255, 255);

        ImageTranslateRenderer.BlendPremultipliedOver(src, 4, 1, 1, dst, 4, 1, 1, 0, 0);

        Assert.AreEqual(191, dst[0]);
        Assert.AreEqual(191, dst[1]);
        Assert.AreEqual(191, dst[2]);
    }

    [TestMethod]
    public void Blend_ClipsOverlayHangingOffBottomRight()
    {
        // 3x3 opaque red overlay placed at (2,2) on a 4x4 image → only 2x2 lands.
        var src = Fill(3, 3, 0, 0, 255, a: 255);
        var dst = Fill(4, 4, 0, 0, 0);

        var dirty = ImageTranslateRenderer.BlendPremultipliedOver(src, 12, 3, 3, dst, 16, 4, 4, 2, 2);

        Assert.AreEqual(new Int32Rect(2, 2, 2, 2), dirty);
        Assert.AreEqual(255, Px(dst, 16, 3, 3)[2]);
        Assert.AreEqual(255, Px(dst, 16, 2, 2)[2]);
        Assert.AreEqual(0, Px(dst, 16, 1, 1)[2]);
    }

    [TestMethod]
    public void Blend_ClipsNegativeOffset()
    {
        var src = Fill(3, 3, 0, 255, 0, a: 255);
        var dst = Fill(4, 4, 0, 0, 0);

        var dirty = ImageTranslateRenderer.BlendPremultipliedOver(src, 12, 3, 3, dst, 16, 4, 4, -2, -1);

        Assert.AreEqual(new Int32Rect(0, 0, 1, 2), dirty);
        Assert.AreEqual(255, Px(dst, 16, 0, 0)[1]);
        Assert.AreEqual(255, Px(dst, 16, 0, 1)[1]);
        Assert.AreEqual(0, Px(dst, 16, 0, 2)[1]);
        Assert.AreEqual(0, Px(dst, 16, 1, 0)[1]);
    }

    [TestMethod]
    public void Blend_FullyOutsideIsNoOp()
    {
        var src = Fill(2, 2, 9, 9, 9, a: 255);
        var dst = Fill(2, 2, 1, 1, 1);
        var before = (byte[])dst.Clone();

        var dirty = ImageTranslateRenderer.BlendPremultipliedOver(src, 8, 2, 2, dst, 8, 2, 2, 5, 5);

        Assert.IsTrue(dirty.IsEmpty);
        CollectionAssert.AreEqual(before, dst);
    }

    [TestMethod]
    public void ComputeBlitRegion_ClipsRotatedBoundsAtEdges()
    {
        // Bounds of a rotated box that spills past left/top and right/bottom.
        var region = ImageTranslateRenderer.ComputeBlitRegion(new Rect(-5.5, -3.2, 40.7, 30.1), 30, 20);
        Assert.AreEqual(new Int32Rect(0, 0, 30, 20), region);

        var inner = ImageTranslateRenderer.ComputeBlitRegion(new Rect(2.4, 3.6, 10.2, 4.1), 30, 20);
        Assert.AreEqual(new Int32Rect(2, 3, 11, 5), inner);
    }

    [TestMethod]
    public void ComputeBlitRegion_EmptyWhenOutside()
    {
        Assert.IsTrue(ImageTranslateRenderer.ComputeBlitRegion(new Rect(40, 40, 5, 5), 30, 20).IsEmpty);
        Assert.IsTrue(ImageTranslateRenderer.ComputeBlitRegion(new Rect(-10, 0, 5, 5), 30, 20).IsEmpty);
        Assert.IsTrue(ImageTranslateRenderer.ComputeBlitRegion(Rect.Empty, 30, 20).IsEmpty);
    }

    [TestMethod]
    public void OffsetQuad_TranslatesAllCorners()
    {
        var quad = new[] { new Point(10, 10), new Point(20, 12), new Point(19, 22), new Point(9, 20) };
        var local = ImageTranslateRenderer.OffsetQuad(quad, -9, -10);
        Assert.AreEqual(new Point(1, 0), local[0]);
        Assert.AreEqual(new Point(11, 2), local[1]);
        Assert.AreEqual(new Point(10, 12), local[2]);
        Assert.AreEqual(new Point(0, 10), local[3]);
    }

    [TestMethod]
    public void Render_RotatedQuadAtImageEdge_KeepsSizeAndLeavesFarPixelsIntact()
    {
        var (w, h, farPixel) = RunOnSta(() =>
        {
            var bmp = SolidBgra(60, 40, 200, 200, 200);
            var corners = new[]
            {
                new Point(-6, 4),
                new Point(30, -5),
                new Point(34, 12),
                new Point(-2, 21),
            };
            var line = new OcrTextLine("源", new Rect(0, 0, 34, 21), 0.9f, corners);
            var result = ImageTranslateRenderer.Render(bmp, new[] { line }, new[] { "Hi" });
            var px = new byte[4];
            result.CopyPixels(new Int32Rect(55, 35, 1, 1), px, 4, 0);
            return (result.PixelWidth, result.PixelHeight, px);
        });

        Assert.AreEqual(60, w);
        Assert.AreEqual(40, h);
        CollectionAssert.AreEqual(new byte[] { 200, 200, 200, 255 }, farPixel);
    }

    private static byte[] Fill(int w, int h, byte b, byte g, byte r, byte a = 255)
    {
        var buf = new byte[w * h * 4];
        for (int i = 0; i < buf.Length; i += 4)
        {
            buf[i] = b;
            buf[i + 1] = g;
            buf[i + 2] = r;
            buf[i + 3] = a;
        }

        return buf;
    }

    private static ReadOnlySpan<byte> Px(byte[] buf, int stride, int x, int y)
        => buf.AsSpan((y * stride) + (x * 4), 4);

    private static BitmapSource SolidBgra(int w, int h, byte b, byte g, byte r)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, Fill(w, h, b, g, r), w * 4);
        bmp.Freeze();
        return bmp;
    }

    private static T RunOnSta<T>(Func<T> action)
    {
        T result = default;
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw failure;
        }

        return result;
    }
}
