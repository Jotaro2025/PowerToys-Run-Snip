// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Community.PowerToys.Run.Plugin.Snip.Models;

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
    /// <exception cref="OcrException">Engine unavailable or recognition failed.</exception>
    public static async Task<IReadOnlyList<OcrTextLine>> RecognizeLinesAsync(
        byte[] png,
        SnipSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (ResolveEngine(settings) == OcrEngineKind.WeChat)
        {
            return await WeChatOcrService.RecognizeLinesAsync(png, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await WindowsOcrService.RecognizeLinesAsync(png, cancellationToken).ConfigureAwait(false);
        }
        catch (OcrException ex) when ((settings?.OcrEngine ?? OcrEngineKind.Auto) == OcrEngineKind.Auto
                                      && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            // Auto on x64 fell back to Windows OCR because WeChat assets are missing: point at the other engine.
            throw new OcrException($"{ex.Message} {Properties.Resources.ocr_hint_switch_wechat}", ex);
        }
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
