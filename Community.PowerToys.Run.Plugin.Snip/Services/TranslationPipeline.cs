// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Image-translate pipeline around an <see cref="ITranslator"/>:
/// identical source strings are translated once per capture (dedupe) and exact
/// repeats across captures are served from <see cref="TranslationMemory"/>.
/// </summary>
public static class TranslationPipeline
{
    /// <summary>
    /// Translates <paramref name="sources"/> preserving order, deduplicating
    /// identical texts and consulting the disk cache when enabled.
    /// </summary>
    public static async Task<IReadOnlyList<string>> TranslateAsync(
        ITranslator translator,
        SnipSettings settings,
        IReadOnlyList<string> sources,
        IProgress<TranslatorProgress> progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(translator);
        ArgumentNullException.ThrowIfNull(sources);
        if (settings == null)
        {
            return await translator.TranslateAsync(sources, "简体中文", progress, cancellationToken).ConfigureAwait(false);
        }

        var results = new string[sources.Count];
        var unique = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < sources.Count; i++)
        {
            string text = sources[i];
            if (string.IsNullOrWhiteSpace(text))
            {
                results[i] = string.Empty;
                continue;
            }

            if (!unique.TryGetValue(text, out var indices))
            {
                indices = new List<int>(1);
                unique[text] = indices;
            }

            indices.Add(i);
        }

        if (unique.Count == 0)
        {
            return results;
        }

        var uniqueTexts = new List<string>(unique.Count);
        uniqueTexts.AddRange(unique.Keys);

        bool useCache = settings.TranslationCacheEnabled;
        string providerKey = TranslationMemory.BuildProviderKey(settings);
        string target = settings.TargetLanguage ?? string.Empty;

        string[] cached = useCache
            ? TranslationMemory.Lookup(uniqueTexts, providerKey, target)
            : new string[uniqueTexts.Count];

        var resolved = new string[uniqueTexts.Count];
        var missing = new List<int>(uniqueTexts.Count);
        int cachedCount = 0;
        for (int i = 0; i < uniqueTexts.Count; i++)
        {
            if (!string.IsNullOrEmpty(cached[i]))
            {
                resolved[i] = cached[i];
                cachedCount++;
            }
            else
            {
                missing.Add(i);
            }
        }

        // Report cache hits immediately so the busy label does not sit at 0/N.
        progress?.Report(new TranslatorProgress(cachedCount, uniqueTexts.Count));

        if (missing.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var missTexts = new List<string>(missing.Count);
            foreach (int index in missing)
            {
                missTexts.Add(uniqueTexts[index]);
            }

            var missProgress = progress == null
                ? null
                : new DelegatingProgress<TranslatorProgress>(p =>
                    progress.Report(new TranslatorProgress(cachedCount + p.Completed, uniqueTexts.Count)));

            var translated = await translator
                .TranslateAsync(missTexts, target, missProgress, cancellationToken)
                .ConfigureAwait(false);

            for (int i = 0; i < missing.Count; i++)
            {
                int index = missing[i];
                resolved[index] = translated != null && i < translated.Count
                    ? translated[i] ?? uniqueTexts[index]
                    : uniqueTexts[index];
            }

            if (useCache)
            {
                var storeSources = new List<string>(missing.Count);
                var storeValues = new List<string>(missing.Count);
                for (int i = 0; i < missing.Count; i++)
                {
                    storeSources.Add(uniqueTexts[missing[i]]);
                    storeValues.Add(resolved[missing[i]]);
                }

                TranslationMemory.Store(storeSources, storeValues, providerKey, target);

                // One write per capture, only when new pairs were added.
                TranslationMemory.Flush();
            }
        }

        for (int i = 0; i < uniqueTexts.Count; i++)
        {
            foreach (int index in unique[uniqueTexts[i]])
            {
                results[index] = resolved[i];
            }
        }

        return results;
    }

    /// <summary>Invokes the handler inline (no <see cref="Progress{T}"/> thread-pool hop).</summary>
    private sealed class DelegatingProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public DelegatingProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler(value);
    }
}
