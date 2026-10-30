// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Text extraction (OCR) through the pure C# <c>Sdcb.SimdPaddleOCR</c> package
/// (PP-OCRv6 DET+CLS+REC). Models ship embedded in the model assembly, so the
/// engine works offline on x64 and ARM64 with no download step.
/// </summary>
/// <remarks>
/// The image is decoded to BGRA32 pixels and handed to the recognizer directly
/// (the package takes interleaved pixel memory, not encoded files). Chinese-only
/// dictionary for now. One recognition at a time: <see cref="PaddleOcrAll"/>
/// reuses pooled inference sessions that are not thread-safe.
/// </remarks>
public static class SimdPaddleOcrService
{
    /// <summary>PaddleOcrAll pools inference sessions; run one recognition at a time.</summary>
    private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Reused OCR pipeline: loading the DET/CLS/REC models costs the most on the
    /// first call. Owned by <see cref="Gate"/>; disposed on plugin teardown.
    /// </summary>
    private static PaddleOcrAll _sharedOcr;

    /// <summary>True when another recognition currently holds the process-wide gate.</summary>
    public static bool IsBusy => Gate.CurrentCount == 0;

    /// <summary>
    /// Recognizes text lines with their axis-aligned boxes (for erase+repaint).
    /// Lines are returned in reading order (top-to-bottom, then left-to-right).
    /// </summary>
    /// <exception cref="OcrException">Model load or recognition failed.</exception>
    public static async Task<IReadOnlyList<OcrTextLine>> RecognizeLinesAsync(
        byte[] png,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);

        // Decode + inference are CPU-bound: keep them off the UI thread.
        return await Task.Run(
            async () =>
            {
                await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return RunOcrLines(png, cancellationToken);
                }
                finally
                {
                    Gate.Release();
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<OcrTextLine> RunOcrLines(byte[] png, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // PngReader hands back a frozen Bgra32 bitmap; copy its bytes so the
            // recognizer owns plain pixel memory (safe across threads, unlike
            // unfrozen bitmap back buffers).
            var source = PngReader.DecodeFrozen(png)
                ?? throw new OcrException(Properties.Resources.ocr_err_failed);
            int stride = source.PixelWidth * 4;
            var pixels = new byte[stride * source.PixelHeight];
            source.CopyPixels(pixels, stride, 0);

            var ocr = GetOrCreateOcr();
            cancellationToken.ThrowIfCancellationRequested();
            PaddleOcrResult result = ocr.Run(
                pixels,
                source.PixelWidth,
                source.PixelHeight,
                stride,
                ImagePixelFormat.Bgra32);
            return ExtractLines(result);
        }
        catch (Exception ex) when (ex is not OcrException && ex is not OperationCanceledException)
        {
            Log.Exception($"Snip Paddle OCR failed: {ex.Message}", ex, typeof(SimdPaddleOcrService));
            throw new OcrException(FailedMessage(ex.Message), ex);
        }
    }

    private static PaddleOcrAll GetOrCreateOcr()
    {
        if (_sharedOcr != null)
        {
            return _sharedOcr;
        }

        // CPU backend on every stage: the GPU paths (Vulkan/Metal) load system
        // graphics drivers in-process, and a driver fault here would take down
        // the PowerToys Run host. Deterministic CPU keeps OCR self-contained.
        //
        // Detector side limit raised to the library maximum (default 960): at
        // 960 a full-screen snip (3440x1440) is downscaled ~3.6x and 14px UI
        // text misreads and splits. At 4096 the same capture reads cleanly
        // (~750 ms); small selections are unaffected either way.
        var options = new PaddleOcrOptions
        {
            Detector = new PaddleOcrDetectorOptions { Backend = OcrBackend.Cpu, LimitSideLength = 4096 },
            Classifier = new PaddleOcrClassifierOptions { Backend = OcrBackend.Cpu },
            Recognizer = new PaddleOcrRecognizerOptions { Backend = OcrBackend.Cpu },
        };
        _sharedOcr = PaddleOcrAll.Load(ChineseV6TinyModels.Default, options);
        Log.Info("Snip Paddle OCR: PP-OCRv6 tiny models loaded.", typeof(SimdPaddleOcrService));
        return _sharedOcr;
    }

    /// <summary>Maps a PaddleOcr result to ordered <see cref="OcrTextLine"/> boxes.</summary>
    public static IReadOnlyList<OcrTextLine> ExtractLines(PaddleOcrResult result)
    {
        var raw = result?.Lines;
        if (raw == null || raw.Length == 0)
        {
            return [];
        }

        var lines = new List<OcrTextLine>(raw.Length);
        foreach (var line in raw)
        {
            var quad = line.Box;
            double left = Math.Min(Math.Min(quad.X1, quad.X2), Math.Min(quad.X3, quad.X4));
            double right = Math.Max(Math.Max(quad.X1, quad.X2), Math.Max(quad.X3, quad.X4));
            double top = Math.Min(Math.Min(quad.Y1, quad.Y2), Math.Min(quad.Y3, quad.Y4));
            double bottom = Math.Max(Math.Max(quad.Y1, quad.Y2), Math.Max(quad.Y3, quad.Y4));
            if (string.IsNullOrEmpty(line.Text) || right <= left || bottom <= top)
            {
                continue;
            }

            lines.Add(new OcrTextLine(
                line.Text,
                new Rect(left, top, right - left, bottom - top),
                NormalizeConfidence(line.RecognitionScore),
                [
                    new Point(quad.X1, quad.Y1),
                    new Point(quad.X2, quad.Y2),
                    new Point(quad.X3, quad.Y3),
                    new Point(quad.X4, quad.Y4),
                ]));
        }

        // Reading order: top-to-bottom, then left-to-right.
        return lines
            .OrderBy(l => l.Box.Top)
            .ThenBy(l => l.Box.Left)
            .ToList();
    }

    /// <summary>
    /// Maps a recognition score to a probability-like 0..1 confidence, matching
    /// the scale WeChat OCR and Windows OCR report. The library's compact ArgMax
    /// path emits the mean pre-softmax activation (a raw logit, regularly above
    /// 1); the dense path emits a proper mean probability in 0..1. Logits are
    /// squashed monotonically so ordering survives the normalization.
    /// </summary>
    private static float NormalizeConfidence(float score)
        => score > 1f
            ? 1f / (1f + MathF.Exp(-Math.Clamp(score, -88f, 88f)))
            : Math.Clamp(score, 0f, 1f);

    /// <summary>User-facing "text extraction failed" message with the technical detail appended.</summary>
    public static string FailedMessage(string detail)
        => $"{Properties.Resources.ocr_err_failed} {detail}";

    /// <summary>
    /// Disposes the shared OCR pipeline (freeing the loaded models). Call on
    /// plugin teardown. Safe to call multiple times; a later recognition simply
    /// reloads the models.
    /// </summary>
    public static void ShutdownOcr()
    {
        if (!Gate.Wait(0))
        {
            // A recognition is in flight; let it finish and dispose its own way.
            return;
        }

        try
        {
            var ocr = Interlocked.Exchange(ref _sharedOcr, null);
            try
            {
                ocr?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Snip Paddle OCR dispose: {ex.Message}", typeof(SimdPaddleOcrService));
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
