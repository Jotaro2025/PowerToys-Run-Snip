// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

using Community.PowerToys.Run.Plugin.Snip.Models;

namespace Community.PowerToys.Run.Plugin.Snip;

/// <summary>
/// Plugin options, persisted by PowerToys Run through the additional options
/// of <see cref="Wox.Plugin.ISettingProvider"/>.
/// </summary>
public sealed class SnipSettings
{
    /// <summary>Directory auto-saved screenshots go to. Empty = Pictures\Snip.</summary>
    public string SaveDirectory { get; set; } = string.Empty;

    /// <summary>Copy the screenshot to the clipboard after confirming.</summary>
    public bool CopyAfterCapture { get; set; } = true;

    /// <summary>Save the screenshot to <see cref="SaveDirectory"/> after confirming.</summary>
    public bool AutoSaveAfterCapture { get; set; }

    /// <summary>Delay before the capture starts, giving PowerToys Run time to hide.</summary>
    public int CaptureDelayMs { get; set; } = 150;

    /// <summary>Translator used by the image-translate toolbar action.</summary>
    public TranslatorProvider TranslatorProvider { get; set; } = TranslatorProvider.Off;

    /// <summary>Target language name passed to the translator prompt (free text).</summary>
    public string TargetLanguage { get; set; } = "简体中文";

    /// <summary>OpenAI-compatible API base URL, e.g. http://127.0.0.1:8080/v1.</summary>
    public string HttpBaseUrl { get; set; } = "http://127.0.0.1:8080/v1";

    /// <summary>Optional Bearer API key for the HTTP translator.</summary>
    public string HttpApiKey { get; set; } = string.Empty;

    /// <summary>Model name sent in the chat completions request.</summary>
    public string HttpModel { get; set; } = "hy-mt2";

    /// <summary>Path to a Hy-MT2 GGUF file (required when provider is HyMT2Sharp).</summary>
    public string HyMtGgufPath { get; set; } = string.Empty;

    /// <summary>HyMT2Sharp backend selection.</summary>
    public HyMtBackendKind HyMtBackend { get; set; } = HyMtBackendKind.Auto;

    /// <summary>Fallback directory when <see cref="SaveDirectory"/> is not set.</summary>
    public static string DefaultSaveFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Snip");
}
