// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.Models;

/// <summary>Which translator backs the image-translate toolbar action.</summary>
public enum TranslatorProvider
{
    /// <summary>Image translate is disabled until the user picks a provider.</summary>
    Off = 0,

    /// <summary>OpenAI-compatible HTTP chat completions (HyMT2Sharp.Server, etc.).</summary>
    HttpOpenAi = 1,

    /// <summary>In-process HyMT2Sharp loaded from a user-supplied GGUF path.</summary>
    HyMT2Sharp = 2,
}
