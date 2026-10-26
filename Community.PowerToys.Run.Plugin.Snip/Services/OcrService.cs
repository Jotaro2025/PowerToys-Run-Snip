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
    /// non-x64 → Windows; explicit choice otherwise; Auto → WeChat when its assets exist, else Windows.
    /// </summary>
    public static OcrEngineKind ResolveEngine(OcrEngineKind setting, Architecture processArchitecture, bool weChatAssetsAvailable)
    {
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
        => ResolveEngine(settings) == OcrEngineKind.WeChat ? WeChatOcrService.IsBusy : WindowsOcrService.IsBusy;

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
        if (ResolveEngine(settings) == OcrEngineKind.WeChat)
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
