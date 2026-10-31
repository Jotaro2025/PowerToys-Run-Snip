// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.IO;
using System.Windows.Media;

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

    /// <summary>Show a PowerToys Run notification after a copy-to-clipboard capture (default off).</summary>
    public bool NotifyOnCopy { get; set; }

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

    /// <summary>OCR engine for text extraction and image translate (see <see cref="OcrEngineKind"/>).</summary>
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.Auto;

    /// <summary>
    /// Warm the effective OCR engine when the snip overlay opens (PaddleOCR
    /// models / WeChatOCR.exe child process), so the first recognition after the
    /// selection does not pay the startup cost. Default on.
    /// </summary>
    public bool PrewarmOcrOnCapture { get; set; } = true;

    /// <summary>
    /// Compute backend for the PaddleOCR engine (see <see cref="OcrBackendKind"/>).
    /// Only used when <see cref="OcrEngine"/> resolves to PaddleOCR. Defaults to
    /// CPU so an upgrade never starts loading a graphics driver in the host
    /// process until the user explicitly opts in.
    /// </summary>
    public OcrBackendKind OcrBackend { get; set; } = OcrBackendKind.Cpu;

    /// <summary>
    /// Windows OCR recognizer language: <c>auto</c> (default) or a BCP-47 tag from installed
    /// OCR languages (e.g. <c>zh-Hans-CN</c>, <c>en-US</c>).
    /// </summary>
    public string WindowsOcrLanguage { get; set; } = "auto";

    /// <summary>Translator used by the image-translate toolbar action.</summary>
    public TranslatorProvider TranslatorProvider { get; set; } = TranslatorProvider.Off;

    /// <summary>Target language name passed to the translator prompt (free text).</summary>
    public string TargetLanguage { get; set; } = "简体中文";

    /// <summary>
    /// Reuse exact source→translation pairs across captures (disk translation
    /// memory). Same-provider/model/target only; default on.
    /// </summary>
    public bool TranslationCacheEnabled { get; set; } = true;

    /// <summary>
    /// Start loading the in-process HyMT model as soon as the image-translate
    /// window opens (in parallel with OCR) instead of waiting for the first
    /// translation request. Default on; other providers need no preload.
    /// </summary>
    public bool PrewarmTranslatorOnTranslate { get; set; } = true;

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

    /// <summary>Tencent Cloud API SecretId (TMT translator).</summary>
    public string TencentSecretId { get; set; } = string.Empty;

    /// <summary>Tencent Cloud API SecretKey (TMT translator).</summary>
    public string TencentSecretKey { get; set; } = string.Empty;

    /// <summary>Tencent Cloud TMT region, e.g. <c>ap-guangzhou</c> or <c>ap-beijing</c>.</summary>
    public string TencentRegion { get; set; } = "ap-guangzhou";

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

    /// <summary>
    /// When true, quietly query GitHub Releases for a newer tag than <c>plugin.json</c>.
    /// Default off; failures (private repo, network) are silent.
    /// </summary>
    public bool CheckForUpdates { get; set; }

    /// <summary>
    /// When true, scroll/stitch diagnostics go to the plugin debug log.
    /// Default off (quiet) — same intent as #54 not to spam.
    /// </summary>
    public bool VerboseScrollLogging { get; set; }

    /// <summary>Last used annotation stroke color in <c>#RRGGBB</c> format.</summary>
    public string LastStrokeColorHex { get; set; } = "#EE204D";

    /// <summary>Last used annotation stroke width in DIPs.</summary>
    public double LastStrokeWidth { get; set; } = 3.0;

    /// <summary>Formats a <see cref="Color"/> as <c>#RRGGBB</c>.</summary>
    public static string FormatColorHex(Color color)
        => string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");

    /// <summary>Parses a <c>#RRGGBB</c> string or returns <paramref name="fallback"/>.</summary>
    public static Color ParseColorHexOrDefault(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        ReadOnlySpan<char> span = hex.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span.Slice(1);
        }

        if (span.Length == 6
            && byte.TryParse(span.Slice(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            && byte.TryParse(span.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            && byte.TryParse(span.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return Color.FromRgb(r, g, b);
        }

        return fallback;
    }

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
            NotifyOnCopy = NotifyOnCopy,
            AutoSaveAfterCapture = AutoSaveAfterCapture,
            CaptureDelayMs = CaptureDelayMs,
            ScrollIntervalMs = ScrollIntervalMs,
            ScrollMaxTiles = ScrollMaxTiles,
            GlobalHotkey = GlobalHotkey,
            OcrEngine = OcrEngine,
            OcrBackend = OcrBackend,
            PrewarmOcrOnCapture = PrewarmOcrOnCapture,
            WindowsOcrLanguage = WindowsOcrLanguage,
            TranslatorProvider = TranslatorProvider,
            TargetLanguage = TargetLanguage,
            TranslationCacheEnabled = TranslationCacheEnabled,
            PrewarmTranslatorOnTranslate = PrewarmTranslatorOnTranslate,
            UiLanguage = UiLanguage,
            HttpBaseUrl = HttpBaseUrl,
            HttpApiKey = HttpApiKey,
            HttpModel = HttpModel,
            TencentSecretId = TencentSecretId,
            TencentSecretKey = TencentSecretKey,
            TencentRegion = TencentRegion,
            HyMtGgufPath = HyMtGgufPath,
            HyMtBackend = HyMtBackend,
            HyMtCustomDownloadUrl = HyMtCustomDownloadUrl,
            HyMtIdleUnloadMinutes = HyMtIdleUnloadMinutes,
            CheckForUpdates = CheckForUpdates,
            VerboseScrollLogging = VerboseScrollLogging,
            LastStrokeColorHex = LastStrokeColorHex,
            LastStrokeWidth = LastStrokeWidth,
        };
    }

    /// <summary>Copies every option from <paramref name="other"/> into this instance.</summary>
    public void CopyFrom(SnipSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        SaveDirectory = other.SaveDirectory;
        CopyAfterCapture = other.CopyAfterCapture;
        NotifyOnCopy = other.NotifyOnCopy;
        AutoSaveAfterCapture = other.AutoSaveAfterCapture;
        CaptureDelayMs = other.CaptureDelayMs;
        ScrollIntervalMs = other.ScrollIntervalMs;
        ScrollMaxTiles = other.ScrollMaxTiles;
        GlobalHotkey = other.GlobalHotkey;
        OcrEngine = other.OcrEngine;
        OcrBackend = other.OcrBackend;
        PrewarmOcrOnCapture = other.PrewarmOcrOnCapture;
        WindowsOcrLanguage = other.WindowsOcrLanguage;
        TranslatorProvider = other.TranslatorProvider;
        TargetLanguage = other.TargetLanguage;
        TranslationCacheEnabled = other.TranslationCacheEnabled;
        PrewarmTranslatorOnTranslate = other.PrewarmTranslatorOnTranslate;
        UiLanguage = other.UiLanguage;
        HttpBaseUrl = other.HttpBaseUrl;
        HttpApiKey = other.HttpApiKey;
        HttpModel = other.HttpModel;
        TencentSecretId = other.TencentSecretId;
        TencentSecretKey = other.TencentSecretKey;
        TencentRegion = other.TencentRegion;
        HyMtGgufPath = other.HyMtGgufPath;
        HyMtBackend = other.HyMtBackend;
        HyMtCustomDownloadUrl = other.HyMtCustomDownloadUrl;
        HyMtIdleUnloadMinutes = other.HyMtIdleUnloadMinutes;
        CheckForUpdates = other.CheckForUpdates;
        VerboseScrollLogging = other.VerboseScrollLogging;
        LastStrokeColorHex = other.LastStrokeColorHex;
        LastStrokeWidth = other.LastStrokeWidth;
    }
}
