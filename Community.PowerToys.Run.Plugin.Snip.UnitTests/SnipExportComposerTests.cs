// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

/// <summary>
/// <see cref="SnipExportComposer"/> builds WPF Freezable-derived bitmaps and must
/// run on STA, matching the snip editor's UI thread.
/// </summary>
[TestClass]
public class SnipExportComposerTests
{
    private static void RunOnStaThread(Action action)
        => RunOnStaThread<object>(() =>
        {
            action();
            return null;
        });

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

        return failure != null ? throw failure : result;
    }

    private static WriteableBitmap MakeSource(int w, int h, byte r, byte g, byte b)
    {
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            pixels[(i * 4) + 0] = b;
            pixels[(i * 4) + 1] = g;
            pixels[(i * 4) + 2] = r;
            pixels[(i * 4) + 3] = 255;
        }

        bmp.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        return bmp;
    }

    private static Color SampleCenter(BitmapSource bitmap)
    {
        int cx = bitmap.PixelWidth / 2;
        int cy = bitmap.PixelHeight / 2;
        byte[] px = new byte[4];
        bitmap.CopyPixels(new Int32Rect(cx, cy, 1, 1), px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }

    [TestMethod]
    public void NoAnnotations_CropsPhysicalPixels()
    {
        RunOnStaThread(() =>
        {
            var source = MakeSource(100, 80, 10, 200, 60);
            var canvas = new AnnotationCanvas();
            var selection = new Rect(10, 20, 50, 40); // DIP == px at scale 1
            var built = SnipExportComposer.BuildResultBitmap(source, canvas, selection, dpiScale: 1.0);

            Assert.AreEqual(50, built.PixelWidth);
            Assert.AreEqual(40, built.PixelHeight);

            // Crop only: content color must match the source fill.
            var c = SampleCenter(built);
            Assert.AreEqual(10, c.R);
            Assert.AreEqual(200, c.G);
            Assert.AreEqual(60, c.B);
        });
    }

    [TestMethod]
    public void DpiSelection_MapsToPhysicalCrop()
    {
        RunOnStaThread(() =>
        {
            var source = MakeSource(200, 160, 240, 130, 20);
            var canvas = new AnnotationCanvas();
            // 100×80 DIP at 150% → 150×120 physical.
            var built = SnipExportComposer.BuildResultBitmap(source, canvas, new Rect(0, 0, 100, 80), dpiScale: 1.5);

            Assert.AreEqual(150, built.PixelWidth);
            Assert.AreEqual(120, built.PixelHeight);
        });
    }

    [TestMethod]
    public void Crop_ClampsToSourceBounds()
    {
        RunOnStaThread(() =>
        {
            var source = MakeSource(60, 60, 5, 5, 5);
            var canvas = new AnnotationCanvas();
            // Selection extends past the source; must clamp to at least 1×1 inside.
            var built = SnipExportComposer.BuildResultBitmap(source, canvas, new Rect(50, 50, 500, 500), dpiScale: 1.0);

            Assert.IsTrue(built.PixelWidth >= 1);
            Assert.IsTrue(built.PixelHeight >= 1);
        });
    }
}
