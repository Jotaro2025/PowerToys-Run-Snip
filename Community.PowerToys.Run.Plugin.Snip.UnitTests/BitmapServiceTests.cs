// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

/// <summary>
/// Bitmap helpers must run on an STA thread, matching how the screenshot editor
/// hosts them at runtime.
/// </summary>
[TestClass]
public class BitmapServiceTests
{
    private static void RunOnStaThread(Action action)
    {
        Exception failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
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
    }

    private static T RunOnStaThread<T>(Func<T> action)
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

    private static BitmapSource CreateTestBitmap(int width, int height)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.CornflowerBlue, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(Brushes.IndianRed, null, new Rect(0, 0, width / 2, height / 2));
        }

        rtb.Render(visual);
        return rtb;
    }

    [TestMethod]
    public void PngEncoderProducesDecodablePng()
    {
        var png = RunOnStaThread(() =>
        {
            var bitmap = CreateTestBitmap(16, 12);
            return PngEncoder.Encode(bitmap);
        });

        Assert.IsTrue(png.Length > 0, "encoded PNG is empty");

        // PNG magic number.
        Assert.AreEqual(0x89, png[0]);
        Assert.AreEqual(0x50, png[1]);
        Assert.AreEqual(0x4E, png[2]);
        Assert.AreEqual(0x47, png[3]);
    }

    [TestMethod]
    public void PngEncoderRoundTripsPixelSize()
    {
        var decoded = RunOnStaThread(() =>
        {
            var bitmap = CreateTestBitmap(24, 18);
            var png = PngEncoder.Encode(bitmap);

            using var ms = new MemoryStream(png);
            var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        });

        Assert.AreEqual(24, decoded.PixelWidth);
        Assert.AreEqual(18, decoded.PixelHeight);
    }

    [TestMethod]
    public void CropReturnsExactPixelSize()
    {
        var (width, height) = RunOnStaThread(() =>
        {
            var bitmap = CreateTestBitmap(40, 30);
            var cropped = BitmapCropper.Crop(bitmap, new Int32Rect(5, 4, 20, 10));
            return (cropped.PixelWidth, cropped.PixelHeight);
        });

        Assert.AreEqual(20, width);
        Assert.AreEqual(10, height);
    }

    [TestMethod]
    public void CropCopiesTheSelectedPixels()
    {
        var (inRedQuadrant, inBlueQuadrant) = RunOnStaThread(() =>
        {
            var bitmap = CreateTestBitmap(40, 30);
            var cropped = new CroppedBitmap(bitmap, new Int32Rect(5, 4, 20, 10));

            // CopyPixels reads whole rows, so stride is the full row width in bytes.
            const int stride = 20 * 4;
            var full = new byte[stride * 10];
            cropped.CopyPixels(full, stride, 0);

            // Crop pixel (0,0) maps to source (5,4): inside the IndianRed quadrant.
            var a = new[] { full[0], full[1], full[2], full[3] };

            // Crop pixel (15,9) maps to source (20,13): inside the CornflowerBlue quadrant.
            int blue = (9 * stride) + (15 * 4);
            var b = new[] { full[blue], full[blue + 1], full[blue + 2], full[blue + 3] };
            return (a, b);
        });

        // BGRA byte order.
        Assert.AreEqual(0x5C, inRedQuadrant[0], "expected IndianRed blue channel");
        Assert.AreEqual(0x5C, inRedQuadrant[1], "expected IndianRed green channel");
        Assert.AreEqual(0xCD, inRedQuadrant[2], "expected IndianRed red channel");

        Assert.AreEqual(0xED, inBlueQuadrant[0], "expected CornflowerBlue blue channel");
        Assert.AreEqual(0x95, inBlueQuadrant[1], "expected CornflowerBlue green channel");
        Assert.AreEqual(0x64, inBlueQuadrant[2], "expected CornflowerBlue red channel");
    }

    [TestMethod]
    public void CropRejectsEmptySelection()
    {
        RunOnStaThread(() =>
        {
            var bitmap = CreateTestBitmap(10, 10);
            _ = Assert.ThrowsException<ArgumentException>(() => BitmapCropper.Crop(bitmap, default));
        });
    }

    [TestMethod]
    public void PngEncoderEncodeAsyncRoundTripsPixelSize()
    {
        var decoded = RunOnStaThread(() =>
        {
            var bitmap = CreateTestBitmap(32, 20);
            var png = PngEncoder.EncodeAsync(bitmap).GetAwaiter().GetResult();

            using var ms = new MemoryStream(png);
            var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        });

        Assert.AreEqual(32, decoded.PixelWidth);
        Assert.AreEqual(20, decoded.PixelHeight);
    }

    [TestMethod]
    public void MaterializeBufferReturnsExactLength()
    {
        using var ms = new MemoryStream();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        ms.Write(payload, 0, payload.Length);
        // Expand capacity past Length so TryGetBuffer segment.Count < Array.Length.
        _ = ms.Capacity;
        var exact = PngEncoder.MaterializeBuffer(ms);
        CollectionAssert.AreEqual(payload, exact);
        Assert.AreEqual(payload.Length, exact.Length);
    }

    [TestMethod]
    public void EncodeToStreamMatchesEncodeBytes()
    {
        RunOnStaThread(() =>
        {
            var bitmap = CreateTestBitmap(16, 12);
            var viaEncode = PngEncoder.Encode(bitmap);
            using var ms = new MemoryStream();
            PngEncoder.EncodeToStream(bitmap, ms);
            CollectionAssert.AreEqual(viaEncode, ms.ToArray());
        });
    }

    [TestMethod]
    public void DibRowFlipMemoryCopyPreservesBgrAndClearsAlpha()
    {
        const int w = 2;
        const int h = 2;
        const int stride = w * 4;
        // Top-down: row0 = red,opaque ; row1 = blue,opaque
        var src = new byte[stride * h];
        src[0] = 0; src[1] = 0; src[2] = 255; src[3] = 255;
        src[4] = 0; src[5] = 0; src[6] = 255; src[7] = 255;
        src[8] = 255; src[9] = 0; src[10] = 0; src[11] = 255;
        src[12] = 255; src[13] = 0; src[14] = 0; src[15] = 255;
        var dst = new byte[stride * h];
        unsafe
        {
            fixed (byte* s = src)
            {
                fixed (byte* d = dst)
                {
                    ClipboardService.CopyTopDownBgraToBottomUpDib(s, d, h, stride);
                }
            }
        }

        // Bottom-up DIB: first stored row is former bottom (blue), alpha cleared.
        Assert.AreEqual(255, dst[0]);
        Assert.AreEqual(0, dst[1]);
        Assert.AreEqual(0, dst[2]);
        Assert.AreEqual(0, dst[3], "alpha cleared");
        Assert.AreEqual(255, dst[8 + 2], "top row red channel in last DIB row");
        Assert.AreEqual(0, dst[8 + 3], "alpha cleared on last row");
    }

    [TestMethod]
    public void CapturedFrameToWriteableBitmapCopiesPixels()
    {
        RunOnStaThread(() =>
        {
            const int w = 4;
            const int h = 2;
            const int stride = w * 4;
            var pixels = new byte[stride * h];

            // Pixel (0,0) = opaque red (BGRA)
            pixels[0] = 0;
            pixels[1] = 0;
            pixels[2] = 255;
            pixels[3] = 255;

            var frame = new CapturedFrame(pixels, w, h, stride);
            var wb = frame.ToWriteableBitmap();
            Assert.AreEqual(w, wb.PixelWidth);
            Assert.AreEqual(h, wb.PixelHeight);

            var roundTrip = new byte[stride * h];
            wb.CopyPixels(roundTrip, stride, 0);
            Assert.AreEqual(255, roundTrip[2], "red channel");
            Assert.AreEqual(255, roundTrip[3], "alpha channel");
        });
    }

    [TestMethod]
    public void CapturedFrameRejectsShortBuffer()
    {
        _ = Assert.ThrowsException<ArgumentException>(
            () => new CapturedFrame(new byte[4], 4, 2, 16));
    }

    [TestMethod]
    public void BitmapPreviewPassesThroughSmallImages()
    {
        RunOnStaThread(() =>
        {
            var bitmap = CreateTestBitmap(64, 48);
            var preview = BitmapPreview.ForOverlay(bitmap);
            Assert.AreSame(bitmap, preview);
            Assert.IsFalse(BitmapPreview.NeedsDownsample(64, 48));
        });
    }

    [TestMethod]
    public void BitmapPreviewDownsamplesLargeImages()
    {
        RunOnStaThread(() =>
        {
            int edge = BitmapPreview.MaxPreviewEdge + 400;
            var bitmap = CreateTestBitmap(edge, 100);
            Assert.IsTrue(BitmapPreview.NeedsDownsample(edge, 100));

            var preview = BitmapPreview.ForOverlay(bitmap);
            Assert.AreNotSame(bitmap, preview);
            Assert.IsTrue(preview.PixelWidth <= BitmapPreview.MaxPreviewEdge);
            Assert.IsTrue(preview.PixelHeight <= BitmapPreview.MaxPreviewEdge);
            Assert.AreEqual(BitmapPreview.MaxPreviewEdge, Math.Max(preview.PixelWidth, preview.PixelHeight));
            Assert.IsTrue(preview.IsFrozen, "downsampled overlay must be materialized + frozen");
            Assert.IsInstanceOfType(preview, typeof(WriteableBitmap));
        });
    }

    [TestMethod]
    public void BitmapPreviewForPinReturnsFrozenIndependentCopy()
    {
        RunOnStaThread(() =>
        {
            var bitmap = CreateTestBitmap(64, 48);
            var pin = BitmapPreview.ForPin(bitmap);
            Assert.AreNotSame(bitmap, pin, "pin must not alias the export bitmap");
            Assert.IsTrue(pin.IsFrozen);
            Assert.IsInstanceOfType(pin, typeof(WriteableBitmap));
            Assert.AreEqual(64, pin.PixelWidth);
            Assert.AreEqual(48, pin.PixelHeight);
        });
    }
}
