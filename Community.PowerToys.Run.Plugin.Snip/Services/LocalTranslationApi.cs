// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>Lifecycle facade for the loopback translation API used by browser extensions.</summary>
public static class LocalTranslationApi
{
    private static readonly object Sync = new();

    private static LocalTranslationApiServer _server;
    private static SnipSettings _settings;
    private static string _serverSignature;

    /// <summary>Applies the current settings (starts, restarts or stops the loopback server).</summary>
    public static void Apply(SnipSettings settings)
    {
        lock (Sync)
        {
            _settings = settings;
            if (settings == null || !settings.LocalApiEnabled)
            {
                StopLocked();
                return;
            }

            string token = settings.LocalApiToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                token = GenerateToken();
                settings.LocalApiToken = token;
                PersistToken(token);
            }

            int port = settings.LocalApiPort is >= 1024 and <= 65535 ? settings.LocalApiPort : 17853;
            var origins = ParseOrigins(settings.LocalApiAllowedOrigins);
            string signature = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{port}|{token}|{string.Join("|", origins)}");

            if (_server != null
                && _server.IsRunning
                && string.Equals(_serverSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            StopLocked();
            var server = new LocalTranslationApiServer(port, token, origins, TranslateWithLocalEngineAsync);
            server.Start();
            _server = server;
            _serverSignature = signature;
        }
    }

    /// <summary>Stops the loopback server (plugin teardown).</summary>
    public static void Stop()
    {
        lock (Sync)
        {
            StopLocked();
        }
    }

    /// <summary>Current server state for the settings window.</summary>
    public static LocalApiStatus GetStatus()
    {
        lock (Sync)
        {
            if (_server == null)
            {
                return new LocalApiStatus(false, false, 0, null);
            }

            return new LocalApiStatus(true, _server.IsRunning, _server.Port, _server.LastError);
        }
    }

    /// <summary>Generates a new random bearer token (48 lowercase hex characters).</summary>
    public static string GenerateToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>Splits the comma/semicolon separated origin allowlist.</summary>
    internal static IReadOnlyList<string> ParseOrigins(string value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void StopLocked()
    {
        var server = _server;
        _server = null;
        _serverSignature = null;
        server?.Dispose();
    }

    private static void PersistToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                SnipSettingsPersister.Persist(
                [
                    SnipSettingsPersister.OptionPatch.Text("LocalApiToken", SecretProtector.Protect(token)),
                ]);
            }
            catch (Exception ex)
            {
                Log.Warn($"Snip local API token persist failed: {ex.Message}", typeof(LocalTranslationApi));
            }
        });
    }

    private static async Task<IReadOnlyList<string>> TranslateWithLocalEngineAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var settings = Volatile.Read(ref _settings);
        if (settings == null || settings.TranslatorProvider != TranslatorProvider.HyMT2Sharp)
        {
            throw new LocalApiUnavailableException(Properties.Resources.local_api_err_provider);
        }

        string resolved = HyMtModelDownloader.ResolveGgufPath(settings);
        if (string.IsNullOrEmpty(resolved))
        {
            throw new LocalApiUnavailableException(Properties.Resources.local_api_err_model);
        }

        HyMT2SharpTranslatorCache.ConfigureIdleUnload(settings.HyMtIdleUnloadMinutes);
        var translator = HyMT2SharpTranslatorCache.GetOrCreate(resolved, settings.HyMtBackend);

        // Per-request target language: the pipeline uses Settings.TargetLanguage
        // for both the prompt and the cache key, so clone instead of mutating.
        var requestSettings = settings.Clone();
        requestSettings.HyMtGgufPath = resolved;
        if (!string.IsNullOrWhiteSpace(targetLanguage))
        {
            requestSettings.TargetLanguage = targetLanguage.Trim();
        }

        return await TranslationPipeline
            .TranslateAsync(translator, requestSettings, texts, progress: null, cancellationToken)
            .ConfigureAwait(false);
    }
}
