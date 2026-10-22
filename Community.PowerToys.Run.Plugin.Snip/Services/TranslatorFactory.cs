// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Community.PowerToys.Run.Plugin.Snip.Models;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>Validates translator settings and builds an <see cref="ITranslator"/>.</summary>
public static class TranslatorFactory
{
    /// <summary>
    /// Returns a localized Chinese/English error when the provider is off or
    /// misconfigured; otherwise <c>null</c>.
    /// </summary>
    public static string Validate(SnipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        switch (settings.TranslatorProvider)
        {
            case TranslatorProvider.Off:
                return Properties.Resources.tr_err_provider_off;

            case TranslatorProvider.HttpOpenAi:
                if (string.IsNullOrWhiteSpace(settings.HttpBaseUrl))
                {
                    return Properties.Resources.tr_err_http_url;
                }

                if (!Uri.TryCreate(settings.HttpBaseUrl.Trim(), UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return Properties.Resources.tr_err_http_url;
                }

                if (string.IsNullOrWhiteSpace(settings.HttpModel))
                {
                    return Properties.Resources.tr_err_http_model;
                }

                if (string.IsNullOrWhiteSpace(settings.TargetLanguage))
                {
                    return Properties.Resources.tr_err_no_language;
                }

                return null;

            case TranslatorProvider.HyMT2Sharp:
            {
                var resolved = HyMtModelDownloader.ResolveGgufPath(settings);
                if (resolved == null)
                {
                    if (string.IsNullOrWhiteSpace(settings.HyMtGgufPath))
                    {
                        return Properties.Resources.tr_err_gguf_path;
                    }

                    return $"{Properties.Resources.tr_err_gguf_missing} {settings.HyMtGgufPath.Trim()}";
                }

                // Keep settings in sync when falling back to the LocalAppData cache.
                settings.HyMtGgufPath = resolved;

                if (string.IsNullOrWhiteSpace(settings.TargetLanguage))
                {
                    return Properties.Resources.tr_err_no_language;
                }

                return null;
            }

            default:
                return Properties.Resources.tr_err_provider_off;
        }
    }

    /// <summary>
    /// Builds a translator for the current settings.
    /// HTTP instances are owned by the caller (dispose after use).
    /// HyMT2Sharp returns a process-wide cached instance — do not dispose;
    /// call <see cref="HyMT2SharpTranslatorCache.Unload"/> on plugin shutdown.
    /// </summary>
    public static ITranslator Create(SnipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var error = Validate(settings);
        if (error != null)
        {
            throw new TranslatorException(error);
        }

        return settings.TranslatorProvider switch
        {
            TranslatorProvider.HttpOpenAi => new HttpOpenAiTranslator(
                settings.HttpBaseUrl.Trim(),
                settings.HttpApiKey?.Trim() ?? string.Empty,
                settings.HttpModel.Trim()),
            TranslatorProvider.HyMT2Sharp => GetHyMt(settings),
            _ => throw new TranslatorException(Properties.Resources.tr_err_provider_off),
        };
    }

    private static HyMT2SharpTranslator GetHyMt(SnipSettings settings)
    {
        HyMT2SharpTranslatorCache.ConfigureIdleUnload(settings.HyMtIdleUnloadMinutes);
        return HyMT2SharpTranslatorCache.GetOrCreate(
            settings.HyMtGgufPath.Trim(),
            settings.HyMtBackend);
    }
}
