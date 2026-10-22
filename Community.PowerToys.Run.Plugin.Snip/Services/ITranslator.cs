// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>Translates short OCR lines / paragraphs into a target language.</summary>
public interface ITranslator
{
    /// <summary>
    /// Translates <paramref name="texts"/> in order into <paramref name="targetLanguage"/>.
    /// Returns one translated string per input (same count and order).
    /// </summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
