// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Routes text extraction / image-translate OCR to WeChat OCR or Windows OCR according to
/// <see cref="SnipSettings.OcrEngine"/> and the process architecture.
/// </summary>
public static class OcrService
{
    /// <summary>
    /// Effective engine (never <see cref="OcrEngineKind.Auto"/>):
    /// explicit Paddle → Paddle on any architecture; non-x64 → Windows; explicit
    /// choice otherwise; Auto → WeChat when its assets exist, else Windows.
    /// </summary>
    public static OcrEngineKind ResolveEngine(OcrEngineKind setting, Architecture processArchitecture, bool weChatAssetsAvailable)
    {
        // PaddleOCR is pure managed code and ships its models embedded, so it is
        // the only engine that also runs on non-x64 besides Windows OCR.
        if (setting == OcrEngineKind.SimdPaddle)
        {
            return OcrEngineKind.SimdPaddle;
        }

        if (processArchitecture != Architecture.X64)
        {
            return OcrEngineKind.Windows;
        }

        return setting switch
        {
            OcrEngineKind.WeChat => OcrEngineKind.WeChat,
            OcrEngineKind.Windows => OcrEngineKind.Windows,
            _ => weChatAssetsAvailable ? OcrEngineKind.WeChat : OcrEngineKind.Windows,
        };
    }

    /// <summary>Effective engine for the current process and installed WeChat OCR assets.</summary>
    public static OcrEngineKind ResolveEngine(SnipSettings settings)
        => ResolveEngine(
            settings?.OcrEngine ?? OcrEngineKind.Auto,
            RuntimeInformation.ProcessArchitecture,
            WeChatOcrService.IsOcrComponentAvailable());

    /// <summary>True when the effective engine is already running another recognition.</summary>
    public static bool IsBusy(SnipSettings settings)
        => ResolveEngine(settings) switch
        {
            OcrEngineKind.WeChat => WeChatOcrService.IsBusy,
            OcrEngineKind.SimdPaddle => SimdPaddleOcrService.IsBusy,
            _ => WindowsOcrService.IsBusy,
        };

    /// <summary>
    /// Warms the effective OCR engine so the first recognition after the user
    /// finishes selecting a region does not pay the startup cost. Blocking:
    /// callers must run this on a background thread (the snip overlay does).
    /// </summary>
    public static void Prewarm(SnipSettings settings)
    {
        if (settings == null || !settings.PrewarmOcrOnCapture)
        {
            return;
        }

        switch (ResolveEngine(settings))
        {
            case OcrEngineKind.SimdPaddle:
                SimdPaddleOcrService.Prewarm(settings.OcrBackend);
                break;
            case OcrEngineKind.WeChat:
                WeChatOcrService.Prewarm();
                break;
            default:
                WindowsOcrService.Prewarm();
                break;
        }
    }

    /// <summary>Recognizes lines with the effective engine.</summary>
    /// <remarks>
    /// Small selections are upscaled before recognition (both engines
    /// under-recognize small text), and a small image that comes back with no
    /// text is retried once at the larger size before giving up. Recognized
    /// boxes / corners are mapped back to source-pixel space either way.
    /// </remarks>
    /// <exception cref="OcrException">Engine unavailable or recognition failed.</exception>
    public static async Task<IReadOnlyList<OcrTextLine>> RecognizeLinesAsync(
        byte[] png,
        SnipSettings settings,
        CancellationToken cancellationToken = default)
    {
        var engine = ResolveEngine(settings);
        if (engine == OcrEngineKind.SimdPaddle)
        {
            return await RecognizeWithSimdPaddleAsync(
                png,
                settings?.OcrBackend ?? OcrBackendKind.Cpu,
                cancellationToken).ConfigureAwait(false);
        }

        if (engine == OcrEngineKind.WeChat)
        {
            return await RecognizeWithWeChatAsync(png, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await RecognizeWithWindowsAsync(png, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OcrException ex) when ((settings?.OcrEngine ?? OcrEngineKind.Auto) == OcrEngineKind.Auto
                                      && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            // Auto on x64 fell back to Windows OCR because WeChat assets are missing: point at the other engine.
            throw new OcrException($"{ex.Message} {Properties.Resources.ocr_hint_switch_wechat}", ex);
        }
    }

    /// <summary>
    /// Recognizes lines from an already-decoded bitmap. PaddleOCR consumes the
    /// pixels directly, so the common full-screen path skips the PNG encode +
    /// decode round trip. Other engines fall back to encoding once here.
    /// </summary>
    /// <exception cref="OcrException">Engine unavailable or recognition failed.</exception>
    public static async Task<IReadOnlyList<OcrTextLine>> RecognizeLinesAsync(
        BitmapSource image,
        SnipSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (ResolveEngine(settings) == OcrEngineKind.SimdPaddle)
        {
            return await RecognizeWithSimdPaddleAsync(
                image,
                settings?.OcrBackend ?? OcrBackendKind.Cpu,
                cancellationToken).ConfigureAwait(false);
        }

        // WeChat writes a file; Windows OCR decodes PNG. Encode lazily here
        // instead of making every caller pay for a PNG they may not need.
        byte[] png = await PngEncoder.EncodeAsync(image, cancellationToken).ConfigureAwait(false);
        return await RecognizeLinesAsync(png, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Mirrors the WeChat path: small selections are upscaled up front (the
    /// detector does not enlarge below its side-length limit either, and a 2x
    /// upscale only ever helps), then boxes are mapped back to source space.
    /// </summary>
    private static async Task<IReadOnlyList<OcrTextLine>> RecognizeWithSimdPaddleAsync(
        byte[] png,
        OcrBackendKind backend,
        CancellationToken cancellationToken)
    {
        var size = PngReader.TryReadSize(png);
        if (size is { Width: > 0, Height: > 0 } && OcrUpscalePolicy.NeedsUpscale(size.Value.Width, size.Value.Height))
        {
            var (upscaled, scale) = await UpscalePngAsync(png, cancellationToken).ConfigureAwait(false);
            if (upscaled != null)
            {
                Log.Info(
                    $"Snip OCR: upscaled small image for recognition (scale {scale:0.##}).",
                    typeof(OcrService));
                var lines = await SimdPaddleOcrService.RecognizeLinesAsync(upscaled, backend, cancellationToken).ConfigureAwait(false);
                return MapLinesToSource(lines, scale);
            }
        }

        return await SimdPaddleOcrService.RecognizeLinesAsync(png, backend, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Bitmap counterpart of the PaddleOCR path (no PNG round trip).</summary>
    private static async Task<IReadOnlyList<OcrTextLine>> RecognizeWithSimdPaddleAsync(
        BitmapSource image,
        OcrBackendKind backend,
        CancellationToken cancellationToken)
    {
        var plan = OcrUpscalePolicy.PlanUpscale(image.PixelWidth, image.PixelHeight);
        if (plan is { Width: > 0, Height: > 0 })
        {
            var scaled = new TransformedBitmap(
                image,
                new ScaleTransform(
                    plan.Value.Width / (double)image.PixelWidth,
                    plan.Value.Height / (double)image.PixelHeight));
            scaled.Freeze();
            double scale = OcrUpscalePolicy.ActualScale(
                image.PixelWidth,
                image.PixelHeight,
                plan.Value.Width,
                plan.Value.Height);
            Log.Info(
                $"Snip OCR: upscaled small image for recognition (scale {scale:0.##}).",
                typeof(OcrService));
            var lines = await SimdPaddleOcrService.RecognizeLinesAsync(scaled, backend, cancellationToken).ConfigureAwait(false);
            return MapLinesToSource(lines, scale);
        }

        return await SimdPaddleOcrService.RecognizeLinesAsync(image, backend, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<OcrTextLine>> RecognizeWithWeChatAsync(
        byte[] png,
        CancellationToken cancellationToken)
    {
        // Small selections are upscaled up front (both engines under-recognize
        // small text; a 2x upscale only ever helps), then boxes are mapped back
        // to source space. Non-PNG bytes skip the size probe and run direct.
        var size = PngReader.TryReadSize(png);
        if (size is { Width: > 0, Height: > 0 } && OcrUpscalePolicy.NeedsUpscale(size.Value.Width, size.Value.Height))
        {
            var (upscaled, scale) = await UpscalePngAsync(png, cancellationToken).ConfigureAwait(false);
            if (upscaled != null)
            {
                Log.Info(
                    $"Snip OCR: upscaled small image for recognition (scale {scale:0.##}).",
                    typeof(OcrService));
                var lines = await WeChatOcrService.RecognizeLinesAsync(upscaled, cancellationToken).ConfigureAwait(false);
                return MapLinesToSource(lines, scale);
            }
        }

        return await WeChatOcrService.RecognizeLinesAsync(png, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<OcrTextLine>> RecognizeWithWindowsAsync(
        byte[] png,
        SnipSettings settings,
        CancellationToken cancellationToken)
    {
        var size = PngReader.TryReadSize(png);
        if (size is { Width: > 0, Height: > 0 } && !OcrUpscalePolicy.NeedsUpscale(size.Value.Width, size.Value.Height))
        {
            return await WindowsOcrService.RecognizeLinesAsync(
                png,
                settings?.WindowsOcrLanguage ?? "auto",
                cancellationToken).ConfigureAwait(false);
        }

        // Small image: upscale up front, then map boxes back to source space.
        var (upscaled, scale) = await UpscalePngAsync(png, cancellationToken).ConfigureAwait(false);
        if (upscaled == null)
        {
            return await WindowsOcrService.RecognizeLinesAsync(
                png,
                settings?.WindowsOcrLanguage ?? "auto",
                cancellationToken).ConfigureAwait(false);
        }

        Log.Info(
            $"Snip OCR: upscaled small image for recognition (scale {scale:0.##}).",
            typeof(OcrService));
        var lines = await WindowsOcrService.RecognizeLinesAsync(
            upscaled,
            settings?.WindowsOcrLanguage ?? "auto",
            cancellationToken).ConfigureAwait(false);
        return MapLinesToSource(lines, scale);
    }

    private static async Task<(byte[] Png, double Scale)> UpscalePngAsync(
        byte[] png,
        CancellationToken cancellationToken)
    {
        var (upscaled, scale) = await Task.Run(
            () =>
            {
                var source = PngReader.DecodeFrozen(png);
                if (source == null)
                {
                    return ((byte[])null, 1.0);
                }

                var plan = OcrUpscalePolicy.PlanUpscale(source.PixelWidth, source.PixelHeight);
                if (plan == null)
                {
                    return ((byte[])null, 1.0);
                }

                var scaled = new TransformedBitmap(
                    source,
                    new ScaleTransform(
                        plan.Value.Width / (double)source.PixelWidth,
                        plan.Value.Height / (double)source.PixelHeight));
                scaled.Freeze();
                return (PngEncoder.Encode(scaled), OcrUpscalePolicy.ActualScale(
                    source.PixelWidth,
                    source.PixelHeight,
                    plan.Value.Width,
                    plan.Value.Height));
            },
            cancellationToken).ConfigureAwait(false);
        return (upscaled, scale);
    }

    private static IReadOnlyList<OcrTextLine> MapLinesToSource(IReadOnlyList<OcrTextLine> lines, double scale)
    {
        if (Math.Abs(scale - 1.0) < 0.0001)
        {
            return lines;
        }

        var mapped = new List<OcrTextLine>(lines.Count);
        foreach (var line in lines)
        {
            mapped.Add(new OcrTextLine(
                line.Text,
                OcrUpscalePolicy.MapBox(line.Box, scale),
                line.Confidence,
                OcrUpscalePolicy.MapCorners(line.Corners, scale)));
        }

        return mapped;
    }

    /// <summary>Recognized text joined by new lines; empty when no text was found.</summary>
    public static async Task<string> RecognizeAsync(byte[] png, SnipSettings settings, CancellationToken cancellationToken = default)
    {
        var lines = await RecognizeLinesAsync(png, settings, cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.AppendLine(line.Text);
        }

        return sb.ToString().TrimEnd();
    }
}
