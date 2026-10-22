// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

using Community.PowerToys.Run.Plugin.Snip.Models;

namespace Community.PowerToys.Run.Plugin.Snip;

/// <summary>
/// Plugin options, persisted by PowerToys Run through the additional options
/// of <see cref="Wox.Plugin.ISettingProvider"/> and the dedicated settings window.
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

    /// <summary>Milliseconds between scroll-capture wheel ticks (50–1000).</summary>
    public int ScrollIntervalMs { get; set; } = 200;

    /// <summary>Maximum raw tiles kept during one scroll session (10–500).</summary>
    public int ScrollMaxTiles { get; set; } = 200;

    /// <summary>
    /// Global hotkey to start a region snip without typing in Run
    /// (e.g. <c>Ctrl+Shift+S</c>). Empty = disabled (default).
    /// </summary>
    public string GlobalHotkey { get; set; } = string.Empty;

    /// <summary>Translator used by the image-translate toolbar action.</summary>
    public TranslatorProvider TranslatorProvider { get; set; } = TranslatorProvider.Off;

    /// <summary>Target language name passed to the translator prompt (free text).</summary>
    public string TargetLanguage { get; set; } = "简体中文";

    /// <summary>
    /// Plugin UI language override.
    /// Empty or <c>system</c> = follow PowerToys / OS UI culture;
    /// <c>en</c> = English; <c>zh-CN</c> = 简体中文.
    /// Independent of <see cref="TargetLanguage"/>.
    /// </summary>
    public string UiLanguage { get; set; } = string.Empty;

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

    /// <summary>
    /// Optional HTTPS URL for the recommended GGUF, tried before ModelScope / mirrors.
    /// Empty keeps the default China-friendly chain.
    /// </summary>
    public string HyMtCustomDownloadUrl { get; set; } = string.Empty;

    /// <summary>Unload the cached HyMT model after this many idle minutes (0 = never, max 240).</summary>
    public int HyMtIdleUnloadMinutes { get; set; } = 10;

    /// <summary>Fallback directory when <see cref="SaveDirectory"/> is not set.</summary>
    public static string DefaultSaveFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Snip");

    /// <summary>Deep-enough copy for the settings editor draft.</summary>
    public SnipSettings Clone()
    {
        return new SnipSettings
        {
            SaveDirectory = SaveDirectory,
            CopyAfterCapture = CopyAfterCapture,
            AutoSaveAfterCapture = AutoSaveAfterCapture,
            CaptureDelayMs = CaptureDelayMs,
            ScrollIntervalMs = ScrollIntervalMs,
            ScrollMaxTiles = ScrollMaxTiles,
            GlobalHotkey = GlobalHotkey,
            TranslatorProvider = TranslatorProvider,
            TargetLanguage = TargetLanguage,
            UiLanguage = UiLanguage,
            HttpBaseUrl = HttpBaseUrl,
            HttpApiKey = HttpApiKey,
            HttpModel = HttpModel,
            HyMtGgufPath = HyMtGgufPath,
            HyMtBackend = HyMtBackend,
            HyMtCustomDownloadUrl = HyMtCustomDownloadUrl,
            HyMtIdleUnloadMinutes = HyMtIdleUnloadMinutes,
        };
    }

    /// <summary>Copies every option from <paramref name="other"/> into this instance.</summary>
    public void CopyFrom(SnipSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        SaveDirectory = other.SaveDirectory;
        CopyAfterCapture = other.CopyAfterCapture;
        AutoSaveAfterCapture = other.AutoSaveAfterCapture;
        CaptureDelayMs = other.CaptureDelayMs;
        ScrollIntervalMs = other.ScrollIntervalMs;
        ScrollMaxTiles = other.ScrollMaxTiles;
        GlobalHotkey = other.GlobalHotkey;
        TranslatorProvider = other.TranslatorProvider;
        TargetLanguage = other.TargetLanguage;
        UiLanguage = other.UiLanguage;
        HttpBaseUrl = other.HttpBaseUrl;
        HttpApiKey = other.HttpApiKey;
        HttpModel = other.HttpModel;
        HyMtGgufPath = other.HyMtGgufPath;
        HyMtBackend = other.HyMtBackend;
        HyMtCustomDownloadUrl = other.HyMtCustomDownloadUrl;
        HyMtIdleUnloadMinutes = other.HyMtIdleUnloadMinutes;
    }
}
