// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

/// <summary>
/// WPF <c>Freezable.Freeze()</c> cascades into child freezables: freezing a
/// <see cref="CroppedBitmap"/> (or <see cref="TransformedBitmap"/>) that chains
/// back to the editor's live <see cref="WriteableBitmap"/> also freezes the
/// source, and the next <c>WriteableBitmap.Lock()</c> (pixel magnifier, live
/// scroll preview) throws InvalidOperationException. These tests pin the
/// contract that shared/export bitmaps never freeze the caller's source.
/// </summary>
[TestClass]
public class FreezeCascadeTests
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

    private static WriteableBitmap CreateLiveSource(int width, int height)
    {
        // Matches the editor's _source: a WriteableBitmap the magnifier and
        // scroll live preview keep Lock()-ing while the overlay is visible.
        var wb = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        wb.Lock();
        try
        {
            wb.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            wb.Unlock();
        }

        return wb;
    }

    private static void AssertStillLockable(WriteableBitmap source, string context)
    {
        // The exact operation that crashed: Lock on a frozen bitmap throws
        // InvalidOperationException ("must have IsFrozen set to false").
        source.Lock();
        source.Unlock();
        Assert.IsFalse(source.IsFrozen, context);
    }

    private static unsafe void PaintRegion(WriteableBitmap wb, int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        wb.Lock();
        try
        {
            int stride = wb.BackBufferStride;
            byte* buf = (byte*)wb.BackBuffer;
            for (int y = y0; y < y0 + h; y++)
            {
                byte* row = buf + ((long)y * stride) + (x0 * 4);
                for (int x = 0; x < w; x++)
                {
                    row[0] = b;
                    row[1] = g;
                    row[2] = r;
                    row[3] = 255;
                    row += 4;
                }
            }

            wb.AddDirtyRect(new Int32Rect(x0, y0, w, h));
        }
        finally
        {
            wb.Unlock();
        }
    }

    private static (byte b, byte g, byte r, byte a) SampleCenterBgra(BitmapSource bmp)
    {
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(bmp.PixelWidth / 2, bmp.PixelHeight / 2, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2], px[3]);
    }

    /// <summary>
    /// Renders <paramref name="source"/> through the WPF compositor path
    /// (Image → RenderTargetBitmap) and samples the center pixel. CopyPixels
    /// alone can report correct data while the rendered result is empty, so
    /// overlay previews must be verified at the render level too.
    /// </summary>
    private static (byte b, byte g, byte r, byte a) RenderSampleCenter(BitmapSource source)
    {
        var image = new System.Windows.Controls.Image
        {
            Source = source,
            Width = source.PixelWidth,
            Height = source.PixelHeight,
        };
        image.Arrange(new Rect(0, 0, source.PixelWidth, source.PixelHeight));

        var rtb = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(image);
        return SampleCenterBgra(rtb);
    }

    [TestMethod]
    public void CroppedBitmapFreezeFreezesChainedSource()
    {
        // Pins the WPF behavior the bug relied on: freeze DOES cascade through
        // CroppedBitmap into the live source. If WPF ever stops cascading, this
        // test failing tells us the defensive checks can be retired.
        RunOnStaThread(() =>
        {
            var source = CreateLiveSource(32, 24);
            var cropped = new CroppedBitmap(source, new Int32Rect(4, 4, 16, 12));
            cropped.Freeze();

            Assert.IsTrue(source.IsFrozen, "Freezable.Freeze() is expected to cascade through CroppedBitmap");
        });
    }

    [TestMethod]
    public void CroppedBitmapCloneFreezeDoesNotFreezeSource()
    {
        // FreezeSnapshot (PngEncoder / ClipboardService) and TranslateResultWindow
        // rely on Clone+Freeze decoupling: the clone must not keep freezing the
        // original chain. If this ever fails, those call sites need the same
        // materialized-copy treatment as the OCR window.
        RunOnStaThread(() =>
        {
            var source = CreateLiveSource(32, 24);
            var cropped = new CroppedBitmap(source, new Int32Rect(4, 4, 16, 12));
            var clone = cropped.Clone();
            clone.Freeze();

            Assert.IsFalse(source.IsFrozen, "Clone+Freeze must not cascade into the original source");
            AssertStillLockable(source, "source must stay lockable after Clone+Freeze");
        });
    }

    [TestMethod]
    public void ForOverlayLargeImageKeepsSourceLockable()
    {
        // The overlay preview path (SnipWindow ctor + RestorePassthroughUi) on
        // monitors longer than MaxPreviewEdge: the returned preview must carry
        // real content, stay frozen, and leave the editor's live source lockable
        // for the pixel magnifier.
        RunOnStaThread(() =>
        {
            int edge = BitmapPreview.MaxPreviewEdge + 400;
            var source = CreateLiveSource(edge, 100);
            PaintRegion(source, 0, 0, edge, 100, 255, 0, 0); // red fill
            var preview = BitmapPreview.ForOverlay(source);

            Assert.AreNotSame(source, preview);
            Assert.IsTrue(preview.IsFrozen, "downsampled preview stays materialized + frozen");
            Assert.IsFalse(source.IsFrozen, "ForOverlay must not freeze the live source");
            AssertStillLockable(source, "source must stay lockable after ForOverlay");

            // Content regression: the preview used to render all-gray when the
            // scale transform chain lost its Freeze before the buffer copy.
            var sample = SampleCenterBgra(preview);
            Assert.IsTrue(sample.r > 180, $"preview center should stay red, got b={sample.b} g={sample.g} r={sample.r} a={sample.a}");

            // Render-level regression (what the user actually sees on the
            // overlay): compositor output must carry the pixels too.
            var rendered = RenderSampleCenter(preview);
            Assert.IsTrue(rendered.r > 180, $"preview must render red on screen, got b={rendered.b} g={rendered.g} r={rendered.r} a={rendered.a}");
        });
    }

    [TestMethod]
    public void MaterializeFrozenDecouplesChainedSource()
    {
        // The OCR window now stores MaterializeFrozen(image) instead of freezing
        // the passed bitmap: the WriteableBitmap copy must decouple the chain so
        // freezing it leaves the editor source lockable — and still carry the
        // cropped pixels.
        RunOnStaThread(() =>
        {
            var source = CreateLiveSource(32, 24);
            PaintRegion(source, 4, 4, 16, 12, 0, 0, 255); // blue crop region
            var cropped = new CroppedBitmap(source, new Int32Rect(4, 4, 16, 12));
            var copy = BitmapPreview.MaterializeFrozen(cropped);

            Assert.IsTrue(copy.IsFrozen);
            Assert.AreEqual(16, copy.PixelWidth);
            Assert.AreEqual(12, copy.PixelHeight);
            Assert.IsFalse(source.IsFrozen, "MaterializeFrozen must decouple the source chain");
            AssertStillLockable(source, "source must stay lockable after MaterializeFrozen");

            var sample = SampleCenterBgra(copy);
            Assert.IsTrue(sample.b > 180, $"copy center should stay blue, got b={sample.b} g={sample.g} r={sample.r} a={sample.a}");
        });
    }

    [TestMethod]
    public void PngEncoderAsyncDoesNotFreezeSource()
    {
        // BuildAndEncodeAsync PNG-encodes the export bitmap while the overlay
        // stays alive (OCR / translate / pin session children); FreezeSnapshot
        // must not freeze the live editor source through the crop chain.
        RunOnStaThread(() =>
        {
            var source = CreateLiveSource(64, 48);
            var cropped = new CroppedBitmap(source, new Int32Rect(2, 2, 60, 44));
            var png = PngEncoder.EncodeAsync(cropped).GetAwaiter().GetResult();

            Assert.IsTrue(png.Length > 0);
            Assert.IsFalse(source.IsFrozen, "PngEncoder must not freeze the live source");
            AssertStillLockable(source, "source must stay lockable after PNG encode");
        });
    }
}
