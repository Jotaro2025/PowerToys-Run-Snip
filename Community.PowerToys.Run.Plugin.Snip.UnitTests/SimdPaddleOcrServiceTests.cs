// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sdcb.SimdPaddleOCR;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class SimdPaddleOcrServiceTests
{
    private static PaddleOcrDetectionBox Quad(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
        => new(x1, y1, x2, y2, x3, y3, x4, y4, 1f);

    private static PaddleOcrLine Line(PaddleOcrDetectionBox box, string text, float score = 0f)
        => new()
        {
            Box = box,
            Text = text,
            RecognitionScore = score,
            ClassificationScore = 0f,
            ClassificationLabel = 0u,
            AppliedRotationDegrees = 0,
            EmittedCount = 0u,
        };

    private static PaddleOcrResult Result(params PaddleOcrLine[] lines)
        => new()
        {
            Lines = lines,
            DetectedCount = lines.Length,
            DetectorResizedWidth = 0,
            DetectorResizedHeight = 0,
        };

    [TestMethod]
    public void ExtractLines_MapsQuadSortsByReadingOrder()
    {
        var result = Result(
            Line(Quad(200, 60, 300, 60, 300, 80, 200, 80), "second", 0.86f),
            Line(Quad(10, 10, 90, 10, 90, 40, 10, 40), "first", 0.97f));

        var lines = SimdPaddleOcrService.ExtractLines(result);

        Assert.HasCount(2, lines);
        Assert.AreEqual("first", lines[0].Text);
        Assert.AreEqual("second", lines[1].Text);
        Assert.AreEqual(new Rect(10, 10, 80, 30), lines[0].Box);
        Assert.AreEqual(0.97f, lines[0].Confidence, 0.0001f);

        // Corners keep the detector quad order (typically TL → TR → BR → BL).
        CollectionAssert.AreEqual(
            new[] { new Point(10, 10), new Point(90, 10), new Point(90, 40), new Point(10, 40) },
            lines[0].Corners.ToArray());
    }

    [TestMethod]
    public void ExtractLines_SkipsEmptyTextAndDegenerateBoxes()
    {
        var result = Result(
            Line(Quad(0, 0, 0, 0, 0, 0, 0, 0), "flat"),
            Line(Quad(0, 0, 100, 0, 100, 30, 0, 30), string.Empty),
            Line(Quad(0, 0, 100, 0, 100, 30, 0, 30), null),
            Line(Quad(5, 5, 60, 5, 60, 25, 5, 25), "kept"));

        var lines = SimdPaddleOcrService.ExtractLines(result);

        Assert.HasCount(1, lines);
        Assert.AreEqual("kept", lines[0].Text);
    }

    [TestMethod]
    public void ExtractLines_NormalizesScoresAboveOne()
    {
        // The library's compact ArgMax path reports mean pre-softmax activations
        // (regularly above 1); those are squashed into 0..1, in-range values pass through.
        var result = Result(
            Line(Quad(0, 0, 100, 0, 100, 30, 0, 30), "logit", 37.08f),
            Line(Quad(0, 40, 100, 40, 100, 70, 0, 70), "prob", 0.86f));

        var lines = SimdPaddleOcrService.ExtractLines(result);

        Assert.HasCount(2, lines);
        Assert.IsTrue(lines[0].Confidence is > 0.9f and <= 1f, $"{lines[0].Confidence}");
        Assert.AreEqual(0.86f, lines[1].Confidence, 0.0001f);
    }

    [TestMethod]
    public void ExtractLines_EmptyResultYieldsEmpty()
    {
        Assert.IsEmpty(SimdPaddleOcrService.ExtractLines(null));
        Assert.IsEmpty(SimdPaddleOcrService.ExtractLines(Result()));
    }

    [TestMethod]
    public async Task RecognizeLines_ReadsRenderedDigits()
    {
        // Real PP-OCRv6 inference on a rendered digit run (tiny models are
        // embedded in the plugin output, so this runs fully offline).
        byte[] png = RenderOnStaThread();
        Assert.IsNotNull(PngReader.TryReadSize(png));

        var lines = await SimdPaddleOcrService.RecognizeLinesAsync(png);

        Assert.IsNotEmpty(lines, "expected at least one recognized line");
        string all = string.Join(" ", lines.Select(l => l.Text));
        StringAssert.Contains(all, "1234");
        string confidences = string.Join(", ", lines.Select(l => $"{l.Text}={l.Confidence}"));
        Assert.IsTrue(lines.All(l => l.Confidence is > 0f and <= 1f), $"unexpected confidence: {confidences}");
    }

    [TestMethod]
    public async Task RecognizeLines_FullScreenSmallTextStaysReadable()
    {
        // Full-screen snip (3440x1440) with 14px UI text: the detector's side
        // limit must not downscale this into mush (at the library default 960
        // the rows misread and split). The service raises it to 4096.
        byte[] png = RenderOnStaThreadTall(3440, 1440, 12, fontSize: 14);

        var lines = await SimdPaddleOcrService.RecognizeLinesAsync(png);

        Assert.IsTrue(lines.Count >= 11, $"expected ~12 rows, got {lines.Count}");
        Assert.IsTrue(
            lines.Count(l => l.Text.Contains("1234")) >= 11,
            $"rows misread: {string.Join(" | ", lines.Select(l => l.Text))}");
    }

    private static byte[] RenderOnStaThreadTall(int width, int height, int rows, int fontSize = 28)
    {
        byte[] png = null;
        var thread = new Thread(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                for (int i = 0; i < rows; i++)
                {
                    var text = new FormattedText(
                        $"Row {i:00} count 1234",
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                        fontSize,
                        Brushes.Black,
                        1.0);
                    dc.DrawText(text, new Point(16, 20 + (i * ((height - 40) / rows))));
                }
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            png = PngEncoder.Encode(bitmap);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return png;
    }

    private static byte[] RenderOnStaThread()
    {
        byte[] png = null;
        var thread = new Thread(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 320, 80));
                var text = new FormattedText(
                    "Snip 1234",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    40,
                    Brushes.Black,
                    1.0);
                dc.DrawText(text, new Point(12, 18));
            }

            var bitmap = new RenderTargetBitmap(320, 80, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            png = PngEncoder.Encode(bitmap);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return png;
    }
}
