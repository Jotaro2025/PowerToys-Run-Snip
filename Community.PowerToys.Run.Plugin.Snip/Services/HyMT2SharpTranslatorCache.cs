// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

using Community.PowerToys.Run.Plugin.Snip.Models;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Process-wide cache for <see cref="HyMT2SharpTranslator"/>. Avoids reloading
/// multi-GB GGUF weights after every image translate. Keyed by (gguf path, backend).
/// The model is not thread-safe; callers share the cached instance's single-flight gate.
/// Call <see cref="Unload"/> from the plugin dispose hook; otherwise process exit cleans up.
/// </summary>
public static class HyMT2SharpTranslatorCache
{
    private static readonly object Sync = new object();
    private static HyMT2SharpTranslator _instance;
    private static string _ggufPath;
    private static HyMtBackendKind _backend;

    /// <summary>
    /// Returns the cached translator for <paramref name="ggufPath"/> + <paramref name="backend"/>.
    /// When the key differs from the current cache, the previous instance is disposed and replaced.
    /// Caller must NOT dispose the returned instance; use <see cref="Unload"/> instead.
    /// </summary>
    public static HyMT2SharpTranslator GetOrCreate(string ggufPath, HyMtBackendKind backend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ggufPath);

        var key = NormalizePath(ggufPath);
        lock (Sync)
        {
            if (_instance != null
                && string.Equals(_ggufPath, key, StringComparison.OrdinalIgnoreCase)
                && _backend == backend)
            {
                return _instance;
            }

            ReplaceUnlocked(key, backend);
            return _instance;
        }
    }

    /// <summary>True when a translator matching the key is currently cached.</summary>
    public static bool IsCached(string ggufPath, HyMtBackendKind backend)
    {
        if (string.IsNullOrWhiteSpace(ggufPath))
        {
            return false;
        }

        var key = NormalizePath(ggufPath);
        lock (Sync)
        {
            return _instance != null
                && string.Equals(_ggufPath, key, StringComparison.OrdinalIgnoreCase)
                && _backend == backend;
        }
    }

    /// <summary>Disposes and clears the cached translator, if any.</summary>
    public static void Unload()
    {
        lock (Sync)
        {
            DisposeUnlocked();
        }
    }

    private static void ReplaceUnlocked(string ggufPath, HyMtBackendKind backend)
    {
        DisposeUnlocked();
        _instance = new HyMT2SharpTranslator(ggufPath, backend);
        _ggufPath = ggufPath;
        _backend = backend;
    }

    private static void DisposeUnlocked()
    {
        if (_instance == null)
        {
            return;
        }

        _instance.Dispose();
        _instance = null;
        _ggufPath = null;
    }

    private static string NormalizePath(string ggufPath)
    {
        var trimmed = ggufPath.Trim();
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (ArgumentException)
        {
            return trimmed;
        }
        catch (NotSupportedException)
        {
            return trimmed;
        }
        catch (PathTooLongException)
        {
            // Keep a trimmed key when the path is malformed; load will fail later with a clear error.
            return trimmed;
        }
    }
}
