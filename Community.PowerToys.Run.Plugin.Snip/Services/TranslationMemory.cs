// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Disk-backed translation memory for image translate. Screenshot text repeats
/// heavily (menu items, buttons, dialog titles), so exact source→translation
/// pairs are cached per provider/model/target language and reused instead of
/// running the model again.
/// </summary>
/// <remarks>
/// The cache is a single JSON file in LocalAppData, capped at
/// <see cref="MaxEntries"/> least-recently-used entries. Writes are explicit
/// (<see cref="Flush"/>) so a batch of stores costs one file write.
/// </remarks>
public static class TranslationMemory
{
    /// <summary>Maximum number of cached pairs; the least recently used are dropped.</summary>
    internal const int MaxEntries = 5000;

    private const string DirectoryName = "Community.PowerToys.Run.Plugin.Snip";

    private static readonly object Sync = new();

    private static Dictionary<string, Entry> _entries;
    private static string _pathOverride;

    /// <summary>Number of cached pairs currently held in memory.</summary>
    public static int Count
    {
        get
        {
            lock (Sync)
            {
                EnsureLoaded();
                return _entries.Count;
            }
        }
    }

    /// <summary>Cache file location (overridable by tests).</summary>
    public static string CacheFilePath
        => _pathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PowerToys",
            "RunPlugins",
            DirectoryName,
            "translation-memory.json");

    /// <summary>
    /// Stable identity of the translation engine + model, so switching model or
    /// provider never serves a translation produced by a different one.
    /// </summary>
    public static string BuildProviderKey(SnipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.TranslatorProvider switch
        {
            TranslatorProvider.HyMT2Sharp => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"hymt:{(string.IsNullOrWhiteSpace(settings.HyMtGgufPath) ? "auto" : Path.GetFileName(settings.HyMtGgufPath))}:{(int)settings.HyMtBackend}"),
            TranslatorProvider.HttpOpenAi => "http:" + (settings.HttpModel ?? string.Empty).Trim(),
            TranslatorProvider.Tencent => "tencent:" + (settings.TencentRegion ?? string.Empty).Trim(),
            _ => "off",
        };
    }

    /// <summary>
    /// Looks up <paramref name="sources"/>; the returned array has a null in
    /// every position that is not cached.
    /// </summary>
    public static string[] Lookup(IReadOnlyList<string> sources, string providerKey, string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var result = new string[sources.Count];
        if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(targetLanguage))
        {
            return result;
        }

        lock (Sync)
        {
            EnsureLoaded();
            long now = DateTime.UtcNow.Ticks;
            for (int i = 0; i < sources.Count; i++)
            {
                string source = sources[i];
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                if (_entries.TryGetValue(BuildKey(providerKey, targetLanguage, source), out var entry))
                {
                    result[i] = entry.Value;
                    entry.LastUsedUtcTicks = now;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Stores translated pairs. Empty values, untranslated fallbacks (same text
    /// as the source) and whitespace-only sources are never cached.
    /// </summary>
    public static void Store(
        IReadOnlyList<string> sources,
        IReadOnlyList<string> translations,
        string providerKey,
        string targetLanguage)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(translations);
        if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(targetLanguage))
        {
            return;
        }

        int count = Math.Min(sources.Count, translations.Count);
        if (count == 0)
        {
            return;
        }

        lock (Sync)
        {
            EnsureLoaded();
            long now = DateTime.UtcNow.Ticks;
            for (int i = 0; i < count; i++)
            {
                string source = sources[i];
                string translated = translations[i];
                if (string.IsNullOrWhiteSpace(source)
                    || string.IsNullOrWhiteSpace(translated)
                    || string.Equals(source.Trim(), translated.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                _entries[BuildKey(providerKey, targetLanguage, source)] = new Entry
                {
                    Value = translated.Trim(),
                    LastUsedUtcTicks = now,
                };
            }

            TrimLocked();
        }
    }

    /// <summary>Writes the cache to disk (no-op when the <paramref name="path"/> directory is unavailable).</summary>
    public static void Flush()
    {
        lock (Sync)
        {
            EnsureLoaded();
            TrimLocked();

            try
            {
                string path = CacheFilePath;
                string directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }

                var list = _entries
                    .Select(kv => new EntryDto
                    {
                        Key = kv.Key,
                        Value = kv.Value.Value,
                        LastUsedUtcTicks = kv.Value.LastUsedUtcTicks,
                    })
                    .ToList();
                string json = JsonSerializer.Serialize(list, SerializerOptions);

                Directory.CreateDirectory(directory);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn($"Snip translation cache write failed: {ex.Message}", typeof(TranslationMemory));
            }
        }
    }

    /// <summary>Drops every cached translation (memory + disk).</summary>
    public static void Clear()
    {
        lock (Sync)
        {
            _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            try
            {
                string path = CacheFilePath;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn($"Snip translation cache delete failed: {ex.Message}", typeof(TranslationMemory));
            }
        }
    }

    /// <summary>Test hook: redirects the cache file so tests never touch the user cache.</summary>
    internal static void SetPathForTests(string path)
    {
        lock (Sync)
        {
            _pathOverride = path;
            _entries = null;
        }
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = false,
    };

    private static string BuildKey(string providerKey, string targetLanguage, string source)
        => providerKey.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "|" + providerKey
            + "|" + targetLanguage
            + "|" + source;

    private static void EnsureLoaded()
    {
        if (_entries != null)
        {
            return;
        }

        _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            string path = CacheFilePath;
            if (!File.Exists(path))
            {
                return;
            }

            var list = JsonSerializer.Deserialize<List<EntryDto>>(File.ReadAllText(path));
            if (list == null)
            {
                return;
            }

            foreach (var dto in list)
            {
                if (string.IsNullOrEmpty(dto?.Key) || string.IsNullOrEmpty(dto.Value))
                {
                    continue;
                }

                _entries[dto.Key] = new Entry
                {
                    Value = dto.Value,
                    LastUsedUtcTicks = dto.LastUsedUtcTicks,
                };
            }

            TrimLocked();
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
        {
            Log.Warn($"Snip translation cache load failed: {ex.Message}", typeof(TranslationMemory));
            _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
    }

    private static void TrimLocked()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        var keep = _entries
            .OrderByDescending(kv => kv.Value.LastUsedUtcTicks)
            .Take(MaxEntries)
            .ToList();
        _entries = keep.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private sealed class Entry
    {
        public string Value { get; set; }

        public long LastUsedUtcTicks { get; set; }
    }

    private sealed class EntryDto
    {
        public string Key { get; set; }

        public string Value { get; set; }

        public long LastUsedUtcTicks { get; set; }
    }
}
