// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>Shared prompt style from HyMT2Sharp docs.</summary>
internal static class TranslatorPrompts
{
    public static string Build(string targetLanguage, string text)
        => $"Translate the following text into {targetLanguage}, without additional explanation：{text}";
}
