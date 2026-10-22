// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Process-wide cache for <see cref="HyMT2SharpTranslator"/>. Avoids reloading
/// multi-GB GGUF weights after every image translate. Keyed by (gguf path, backend).
/// The model is not thread-safe; callers share the cached instance's single-flight gate.
/// Call <see cref="Unload"/> from the plugin dispose hook; otherwise process exit cleans up.
/// Optional idle auto-unload (<see cref="ConfigureIdleUnload(int)"/>) frees weights after N idle minutes.
/// </summary>
public static class HyMT2SharpTranslatorCache
{
    private static readonly object Sync = new object();
    private static HyMT2SharpTranslator _instance;
    private static string _ggufPath;
    private static HyMtBackendKind _backend;
    private static TimeSpan _idleTimeout = TimeSpan.FromMinutes(DefaultIdleUnloadMinutes);
    private static DateTime _lastTouchUtc = DateTime.UtcNow;
    private static Timer _idleTimer;

    /// <summary>Default idle minutes before the cached model is unloaded (0 = never).</summary>
    public const int DefaultIdleUnloadMinutes = 10;

    /// <summary>Upper bound accepted by <see cref="ConfigureIdleUnload(int)"/>.</summary>
    public const int MaxIdleUnloadMinutes = 240;

    /// <summary>Current idle timeout; <see cref="TimeSpan.Zero"/> means auto-unload is off.</summary>
    public static TimeSpan IdleTimeout
    {
        get
        {
            lock (Sync)
            {
                return _idleTimeout;
            }
        }
    }

    /// <summary>Sets idle auto-unload in minutes (0 = off, clamped to 0–<see cref="MaxIdleUnloadMinutes"/>).</summary>
    public static void ConfigureIdleUnload(int minutes)
    {
        int m = Math.Clamp(minutes, 0, MaxIdleUnloadMinutes);
        ConfigureIdleUnload(m == 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(m));
    }

    /// <summary>Test/advanced hook: exact idle timeout (<see cref="TimeSpan.Zero"/> = off).</summary>
    internal static void ConfigureIdleUnload(TimeSpan timeout)
    {
        lock (Sync)
        {
            _idleTimeout = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout;
            RescheduleUnlocked(_idleTimeout);
        }
    }

    /// <summary>
    /// Unloads the cached model when it has been idle for at least <see cref="IdleTimeout"/>
    /// at <paramref name="nowUtc"/>. Never unloads while a translation holds the gate.
    /// Returns true when the model was unloaded.
    /// </summary>
    internal static bool TryUnloadIfIdle(DateTime nowUtc)
    {
        lock (Sync)
        {
            if (_instance == null || _idleTimeout <= TimeSpan.Zero)
            {
                return false;
            }

            if (_instance.IsBusy)
            {
                _lastTouchUtc = nowUtc;
                RescheduleUnlocked(_idleTimeout);
                return false;
            }

            var last = _instance.LastUsedUtc > _lastTouchUtc ? _instance.LastUsedUtc : _lastTouchUtc;
            var idle = nowUtc - last;
            if (idle < _idleTimeout)
            {
                RescheduleUnlocked(_idleTimeout - idle);
                return false;
            }

            Log.Info($"Snip HyMT2Sharp idle {idle.TotalMinutes:F1} min — unloading cached model.", typeof(HyMT2SharpTranslatorCache));
            DisposeUnlocked();
            return true;
        }
    }

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
            _lastTouchUtc = DateTime.UtcNow;
            if (_instance != null
                && string.Equals(_ggufPath, key, StringComparison.OrdinalIgnoreCase)
                && _backend == backend)
            {
                RescheduleUnlocked(_idleTimeout);
                return _instance;
            }

            ReplaceUnlocked(key, backend);
            RescheduleUnlocked(_idleTimeout);
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
        _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private static void RescheduleUnlocked(TimeSpan dueIn)
    {
        if (_instance == null || _idleTimeout <= TimeSpan.Zero)
        {
            _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        if (dueIn < TimeSpan.FromSeconds(1))
        {
            dueIn = TimeSpan.FromSeconds(1);
        }

        _idleTimer ??= new Timer(static _ => OnIdleTimer(), null, Timeout.Infinite, Timeout.Infinite);
        _idleTimer.Change(dueIn, Timeout.InfiniteTimeSpan);
    }

    private static void OnIdleTimer()
    {
        try
        {
            TryUnloadIfIdle(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip HyMT2Sharp idle unload failed: {ex.Message}", ex, typeof(HyMT2SharpTranslatorCache));
        }
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
