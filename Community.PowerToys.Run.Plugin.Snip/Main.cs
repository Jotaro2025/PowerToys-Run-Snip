// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Community.PowerToys.Run.Plugin.Snip.UI;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using Wox.Plugin;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip;

/// <summary>
/// PowerToys Run plugin that exposes the region screenshot editor
/// ported from the TDS screenshot tool.
/// </summary>
public class Main : IPlugin, IPluginI18n, ISettingProvider, IDisposable
{
    // Option keys are persisted in PowerToys settings; keep them stable.
    private const string OptionSaveDirectory = "SaveDirectory";
    private const string OptionCopyAfterCapture = "CopyAfterCapture";
    private const string OptionAutoSaveAfterCapture = "AutoSaveAfterCapture";
    private const string OptionCaptureDelayMs = "CaptureDelayMs";
    private const string OptionScrollIntervalMs = "ScrollIntervalMs";
    private const string OptionScrollMaxTiles = "ScrollMaxTiles";
    private const string OptionUnloadHyMt = "UnloadHyMtModel";
    private const string OptionHyMtIdleUnloadMinutes = "HyMtIdleUnloadMinutes";
    private const string OptionGlobalHotkey = "GlobalHotkey";
    private const string OptionTranslatorProvider = "TranslatorProvider";
    private const string OptionOcrEngine = "OcrEngine";
    private const string OptionWindowsOcrLanguage = "WindowsOcrLanguage";
    private const string OptionTargetLanguage = "TargetLanguage";
    private const string OptionUiLanguage = "UiLanguage";
    private const string OptionHttpBaseUrl = "HttpBaseUrl";
    private const string OptionHttpApiKey = "HttpApiKey";
    private const string OptionHttpModel = "HttpModel";
    private const string OptionHyMtGgufPath = "HyMtGgufPath";
    private const string OptionHyMtBackend = "HyMtBackend";
    private const string OptionDownloadHyMt = "DownloadRecommendedHyMtModel";
    private const string OptionHyMtCustomDownloadUrl = "HyMtCustomDownloadUrl";
    private const string OptionCheckForUpdates = "CheckForUpdates";
    private const string OptionVerboseScrollLogging = "VerboseScrollLogging";

    private PluginInitContext _context;
    private string _iconPath;
    private SnipSettings _settings = new SnipSettings();
    private GlobalHotkeyService _hotkeyService;
    private bool _disposed;

    public string Name => Properties.Resources.plugin_name;

    public string Description => Properties.Resources.plugin_description;

    public static string PluginID => "7667E8C5E74948F7B2D6B5E7CED60783";

    public IEnumerable<PluginAdditionalOption> AdditionalOptions => new List<PluginAdditionalOption>()
    {
        new PluginAdditionalOption()
        {
            Key = OptionSaveDirectory,
            DisplayLabel = Properties.Resources.opt_save_dir_label,
            DisplayDescription = Properties.Resources.opt_save_dir_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = _settings.SaveDirectory,
            PlaceholderText = SnipSettings.DefaultSaveFolder,
        },
        new PluginAdditionalOption()
        {
            Key = OptionCopyAfterCapture,
            DisplayLabel = Properties.Resources.opt_copy_label,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Checkbox,
            Value = _settings.CopyAfterCapture,
        },
        new PluginAdditionalOption()
        {
            Key = OptionAutoSaveAfterCapture,
            DisplayLabel = Properties.Resources.opt_autosave_label,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Checkbox,
            Value = _settings.AutoSaveAfterCapture,
        },
        new PluginAdditionalOption()
        {
            Key = OptionCaptureDelayMs,
            DisplayLabel = Properties.Resources.opt_delay_label,
            DisplayDescription = Properties.Resources.opt_delay_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Numberbox,
            NumberValue = _settings.CaptureDelayMs,
            NumberBoxMin = 0,
            NumberBoxMax = 2000,
            NumberBoxSmallChange = 10,
            NumberBoxLargeChange = 100,
        },
        new PluginAdditionalOption()
        {
            Key = OptionScrollIntervalMs,
            DisplayLabel = Properties.Resources.opt_scroll_interval_label,
            DisplayDescription = Properties.Resources.opt_scroll_interval_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Numberbox,
            NumberValue = _settings.ScrollIntervalMs,
            NumberBoxMin = 50,
            NumberBoxMax = 1000,
            NumberBoxSmallChange = 10,
            NumberBoxLargeChange = 50,
        },
        new PluginAdditionalOption()
        {
            Key = OptionScrollMaxTiles,
            DisplayLabel = Properties.Resources.opt_scroll_maxtiles_label,
            DisplayDescription = Properties.Resources.opt_scroll_maxtiles_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Numberbox,
            NumberValue = _settings.ScrollMaxTiles,
            NumberBoxMin = 10,
            NumberBoxMax = 500,
            NumberBoxSmallChange = 10,
            NumberBoxLargeChange = 50,
        },
        new PluginAdditionalOption()
        {
            Key = OptionUnloadHyMt,
            DisplayLabel = Properties.Resources.opt_unload_hymt_label,
            DisplayDescription = Properties.Resources.opt_unload_hymt_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Checkbox,
            Value = false,
        },
        new PluginAdditionalOption()
        {
            Key = OptionHyMtIdleUnloadMinutes,
            DisplayLabel = Properties.Resources.opt_hymt_idle_unload_label,
            DisplayDescription = Properties.Resources.opt_hymt_idle_unload_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Numberbox,
            NumberValue = _settings.HyMtIdleUnloadMinutes,
            NumberBoxMin = 0,
            NumberBoxMax = HyMT2SharpTranslatorCache.MaxIdleUnloadMinutes,
            NumberBoxSmallChange = 1,
            NumberBoxLargeChange = 10,
        },
        new PluginAdditionalOption()
        {
            Key = OptionUiLanguage,
            DisplayLabel = Properties.Resources.opt_ui_lang_label,
            DisplayDescription = Properties.Resources.opt_ui_lang_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = UiLanguageHelper.ToPersistedValue(_settings.UiLanguage),
            PlaceholderText = "system | en | zh-CN",
        },
        new PluginAdditionalOption()
        {
            Key = OptionGlobalHotkey,
            DisplayLabel = Properties.Resources.opt_hotkey_label,
            DisplayDescription = Properties.Resources.opt_hotkey_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = _settings.GlobalHotkey,
            PlaceholderText = Properties.Resources.opt_hotkey_placeholder,
        },

        new PluginAdditionalOption()
        {
            Key = OptionOcrEngine,
            DisplayLabel = Properties.Resources.opt_ocr_engine_label,
            DisplayDescription = Properties.Resources.opt_ocr_engine_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Combobox,
            ComboBoxValue = (int)_settings.OcrEngine,
            ComboBoxItems =
            [
                new KeyValuePair<string, string>(Properties.Resources.opt_ocr_engine_auto, "0"),
                new KeyValuePair<string, string>(Properties.Resources.opt_ocr_engine_wechat, "1"),
                new KeyValuePair<string, string>(Properties.Resources.opt_ocr_engine_windows, "2"),
            ],
        },
        new PluginAdditionalOption()
        {
            Key = OptionWindowsOcrLanguage,
            DisplayLabel = Properties.Resources.opt_windows_ocr_lang_label,
            DisplayDescription = Properties.Resources.opt_windows_ocr_lang_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = string.IsNullOrWhiteSpace(_settings.WindowsOcrLanguage) ? "auto" : _settings.WindowsOcrLanguage,
            PlaceholderText = "auto | zh-Hans-CN | en-US",
        },

        // PowerToys Run AdditionalOptions has no IsVisible / conditional-fold API
        // (settings UI shows every option). Engine-specific fields use «HTTP ·» /
        // «HyMT ·» label prefixes so Off / HTTP / HyMT groups stay scannable.
        // Prefer the dedicated settings window (Run → Settings) which folds fields.
        new PluginAdditionalOption()
        {
            Key = OptionTranslatorProvider,
            DisplayLabel = Properties.Resources.opt_tr_provider_label,
            DisplayDescription = Properties.Resources.opt_tr_provider_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Combobox,
            ComboBoxValue = (int)_settings.TranslatorProvider,
            ComboBoxItems =
            [
                new KeyValuePair<string, string>(Properties.Resources.opt_tr_provider_off, "0"),
                new KeyValuePair<string, string>(Properties.Resources.opt_tr_provider_http, "1"),
                new KeyValuePair<string, string>(Properties.Resources.opt_tr_provider_hymt, "2"),
            ],
        },
        new PluginAdditionalOption()
        {
            Key = OptionTargetLanguage,
            DisplayLabel = Properties.Resources.opt_tr_lang_label,
            DisplayDescription = Properties.Resources.opt_tr_lang_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = _settings.TargetLanguage,
            PlaceholderText = "简体中文",
        },
        new PluginAdditionalOption()
        {
            Key = OptionHttpBaseUrl,
            DisplayLabel = Properties.Resources.opt_tr_http_url_label,
            DisplayDescription = Properties.Resources.opt_tr_http_url_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = _settings.HttpBaseUrl,
            PlaceholderText = "http://127.0.0.1:8080/v1",
        },
        new PluginAdditionalOption()
        {
            Key = OptionHttpApiKey,
            DisplayLabel = Properties.Resources.opt_tr_http_key_label,
            DisplayDescription = Properties.Resources.opt_tr_http_key_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,

            // Never dump DPAPI ciphertext into the PT textbox. Empty = keep existing key on save.
            TextValue = string.Empty,
            PlaceholderText = Properties.Resources.opt_tr_http_key_placeholder,
        },
        new PluginAdditionalOption()
        {
            Key = OptionHttpModel,
            DisplayLabel = Properties.Resources.opt_tr_http_model_label,
            DisplayDescription = Properties.Resources.opt_tr_http_model_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = _settings.HttpModel,
            PlaceholderText = "hy-mt2",
        },
        new PluginAdditionalOption()
        {
            Key = OptionHyMtGgufPath,
            DisplayLabel = Properties.Resources.opt_tr_hymt_path_label,
            DisplayDescription = Properties.Resources.opt_tr_hymt_path_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = string.IsNullOrWhiteSpace(_settings.HyMtGgufPath)
                && HyMtModelDownloader.IsValidCachedFile(HyMtModelDownloader.DefaultCachePath)
                ? HyMtModelDownloader.DefaultCachePath
                : _settings.HyMtGgufPath,
            PlaceholderText = HyMtModelDownloader.DefaultCachePath,
        },
        new PluginAdditionalOption()
        {
            Key = OptionDownloadHyMt,
            DisplayLabel = Properties.Resources.opt_tr_hymt_download_label,
            DisplayDescription = Properties.Resources.opt_tr_hymt_download_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Checkbox,
            Value = false,
        },
        new PluginAdditionalOption()
        {
            Key = OptionHyMtCustomDownloadUrl,
            DisplayLabel = Properties.Resources.opt_tr_hymt_custom_url_label,
            DisplayDescription = Properties.Resources.opt_tr_hymt_custom_url_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = _settings.HyMtCustomDownloadUrl,
            PlaceholderText = "https://…/Hy-MT2-1.8B-2Bit.gguf",
        },
        new PluginAdditionalOption()
        {
            Key = OptionHyMtBackend,
            DisplayLabel = Properties.Resources.opt_tr_hymt_backend_label,
            DisplayDescription = Properties.Resources.opt_tr_hymt_backend_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Combobox,
            ComboBoxValue = (int)_settings.HyMtBackend,
            ComboBoxItems =
            [
                new KeyValuePair<string, string>(Properties.Resources.opt_tr_hymt_backend_auto, "0"),
                new KeyValuePair<string, string>(Properties.Resources.opt_tr_hymt_backend_cpu, "1"),
                new KeyValuePair<string, string>(Properties.Resources.opt_tr_hymt_backend_vulkan, "2"),
            ],
        },
        new PluginAdditionalOption()
        {
            Key = OptionCheckForUpdates,
            DisplayLabel = Properties.Resources.opt_check_updates_label,
            DisplayDescription = Properties.Resources.opt_check_updates_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Checkbox,
            Value = _settings.CheckForUpdates,
        },
        new PluginAdditionalOption()
        {
            Key = OptionVerboseScrollLogging,
            DisplayLabel = Properties.Resources.opt_verbose_scroll_label,
            DisplayDescription = Properties.Resources.opt_verbose_scroll_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Checkbox,
            Value = _settings.VerboseScrollLogging,
        },
    };

    public List<Result> Query(Query query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var commands = new List<Result>
        {
            CreateRegionCommand(
                Properties.Resources.cmd_region_title,
                Properties.Resources.cmd_region_subtitle),
            CreateCaptureCommand(
                Properties.Resources.cmd_window_title,
                Properties.Resources.cmd_window_subtitle,
                CaptureKind.ActiveWindow),
            CreateCaptureCommand(
                Properties.Resources.cmd_fullscreen_title,
                Properties.Resources.cmd_fullscreen_subtitle,
                CaptureKind.FullScreen),
            CreateSettingsCommand(
                Properties.Resources.cmd_settings_title,
                Properties.Resources.cmd_settings_subtitle),
        };

        var search = query.Search?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(search))
        {
            return commands;
        }

        var filtered = commands
            .Where(r => r.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || (r.SubTitle?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        return filtered;
    }

    /// <summary>
    /// Re-saves a legacy plaintext HTTP API key encrypted (DPAPI CurrentUser). Runs off the
    /// calling thread; the resulting settings.json change re-enters UpdateSettings with the
    /// protected value, which needs no further migration.
    /// </summary>
    private static void MigrateApiKeyToProtected(string plainApiKey)
    {
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var patch = SnipSettingsPersister.OptionPatch.Text(OptionHttpApiKey, SecretProtector.Protect(plainApiKey));
                if (SnipSettingsPersister.Persist([patch]))
                {
                    Log.Info("Snip: migrated plaintext HTTP API key to DPAPI-protected storage.", typeof(Main));
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"Snip: API key migration failed: {ex.Message}", ex, typeof(Main));
            }
        });
    }

    /// <summary>
    /// Re-writes the protected form of an already-known plaintext key so PowerToys saving an
    /// empty AdditionalOptions TextValue cannot clear settings.json.
    /// </summary>
    private static void EnsureProtectedApiKeyPersisted(string plainApiKey)
    {
        if (string.IsNullOrEmpty(plainApiKey))
        {
            return;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var patch = SnipSettingsPersister.OptionPatch.Text(OptionHttpApiKey, SecretProtector.Protect(plainApiKey));
                SnipSettingsPersister.Persist([patch]);
            }
            catch (Exception ex)
            {
                Log.Exception($"Snip: re-persist protected HTTP API key failed: {ex.Message}", ex, typeof(Main));
            }
        });
    }

    private Result CreateRegionCommand(string title, string subtitle)
    {
        return CreateCaptureCommand(title, subtitle, CaptureKind.Region);
    }

    private Result CreateCaptureCommand(string title, string subtitle, CaptureKind kind)
    {
        return new Result
        {
            Title = title,
            SubTitle = subtitle,
            IcoPath = _iconPath,
            QueryTextDisplay = title,
            Action = _ =>
            {
                SnipController.Start(_settings, Notify, kind);
                return true;
            },
        };
    }

    private Result CreateSettingsCommand(string title, string subtitle)
    {
        return new Result
        {
            Title = title,
            SubTitle = subtitle,
            IcoPath = _iconPath,
            QueryTextDisplay = title,
            Action = _ =>
            {
                OpenSettingsWindow();
                return true;
            },
        };
    }

    public void Init(PluginInitContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _context.API.ThemeChanged += OnThemeChanged;
        UpdateIconPath(_context.API.GetCurrentTheme());
        UiLanguageHelper.Apply(_settings.UiLanguage);
        SnipVerbose.ScrollLogging = _settings.VerboseScrollLogging;
        ApplyHotkeyRegistration();
        UpdateChecker.MaybeStart(_settings.CheckForUpdates, UpdateChecker.DefaultReleasesApi, Notify);

        // Bootstrap the snip STA (dispatcher + DPI promotion + WPF Application)
        // and the WinRT capture D3D device after Run has settled, so the first
        // Alt+A skips both cold starts. Device prewarm runs off the STA thread
        // (plain thread-pool work, no Dispatcher affinity needed).
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                SnipController.PrewarmUiThread();
                Core.WinRtGraphicsCapture.PrewarmDevice();
            }
            catch (Exception ex)
            {
                Log.Warn($"Snip prewarm failed: {ex.Message}", typeof(Main));
            }
        });
    }

    public string GetTranslatedPluginTitle() => Properties.Resources.plugin_name;

    public string GetTranslatedPluginDescription() => Properties.Resources.plugin_description;

    public Control CreateSettingPanel()
    {
        // Prefer Run → Settings command + AdditionalOptions on the PowerToys page.
        // Returning a custom panel is optional and easy to break across PT versions.
        throw new NotImplementedException();
    }

    public void UpdateSettings(PowerLauncherPluginSettings settings)
    {
        var options = settings?.AdditionalOptions;
        if (options == null)
        {
            return;
        }

        _settings.SaveDirectory = options.FirstOrDefault(x => x.Key == OptionSaveDirectory)?.TextValue ?? string.Empty;
        _settings.CopyAfterCapture = options.FirstOrDefault(x => x.Key == OptionCopyAfterCapture)?.Value ?? true;
        _settings.AutoSaveAfterCapture = options.FirstOrDefault(x => x.Key == OptionAutoSaveAfterCapture)?.Value ?? false;

        var delay = options.FirstOrDefault(x => x.Key == OptionCaptureDelayMs)?.NumberValue;
        if (delay.HasValue && delay.Value >= 0)
        {
            _settings.CaptureDelayMs = (int)Math.Clamp(delay.Value, 0, 2000);
        }

        var scrollInterval = options.FirstOrDefault(x => x.Key == OptionScrollIntervalMs)?.NumberValue;
        if (scrollInterval.HasValue)
        {
            _settings.ScrollIntervalMs = (int)Math.Clamp(scrollInterval.Value, 50, 1000);
        }

        var scrollMax = options.FirstOrDefault(x => x.Key == OptionScrollMaxTiles)?.NumberValue;
        if (scrollMax.HasValue)
        {
            _settings.ScrollMaxTiles = (int)Math.Clamp(scrollMax.Value, 10, 500);
        }

        var idleUnload = options.FirstOrDefault(x => x.Key == OptionHyMtIdleUnloadMinutes)?.NumberValue;
        if (idleUnload.HasValue)
        {
            _settings.HyMtIdleUnloadMinutes = (int)Math.Clamp(idleUnload.Value, 0, HyMT2SharpTranslatorCache.MaxIdleUnloadMinutes);
        }

        HyMT2SharpTranslatorCache.ConfigureIdleUnload(_settings.HyMtIdleUnloadMinutes);
        WeChatOcrService.ConfigureIdleRecycle(_settings.HyMtIdleUnloadMinutes);

        _settings.CheckForUpdates = options.FirstOrDefault(x => x.Key == OptionCheckForUpdates)?.Value ?? false;
        _settings.VerboseScrollLogging = options.FirstOrDefault(x => x.Key == OptionVerboseScrollLogging)?.Value ?? false;
        SnipVerbose.ScrollLogging = _settings.VerboseScrollLogging;

        var strokeHex = options.FirstOrDefault(x => x.Key == "LastStrokeColorHex")?.TextValue;
        if (!string.IsNullOrWhiteSpace(strokeHex))
        {
            _settings.LastStrokeColorHex = strokeHex.Trim();
        }

        var strokeWidth = options.FirstOrDefault(x => x.Key == "LastStrokeWidth")?.NumberValue;
        if (strokeWidth.HasValue && strokeWidth.Value > 0)
        {
            _settings.LastStrokeWidth = Math.Clamp(strokeWidth.Value, 1.0, 24.0);
        }

        var unloadHyMt = options.FirstOrDefault(x => x.Key == OptionUnloadHyMt);
        if (unloadHyMt is { Value: true })
        {
            unloadHyMt.Value = false;
            HyMT2SharpTranslatorCache.Unload();
            Notify(Properties.Resources.opt_unload_hymt_done, string.Empty);
        }

        var hotkey = options.FirstOrDefault(x => x.Key == OptionGlobalHotkey)?.TextValue;
        _settings.GlobalHotkey = hotkey != null ? HotkeyParser.Normalize(hotkey) : string.Empty;

        var ocrOpt = options.FirstOrDefault(x => x.Key == OptionOcrEngine);
        if (ocrOpt != null && Enum.IsDefined(typeof(OcrEngineKind), ocrOpt.ComboBoxValue))
        {
            _settings.OcrEngine = (OcrEngineKind)ocrOpt.ComboBoxValue;
        }

        var ocrLang = options.FirstOrDefault(x => x.Key == OptionWindowsOcrLanguage)?.TextValue?.Trim();
        if (!string.IsNullOrEmpty(ocrLang))
        {
            _settings.WindowsOcrLanguage = ocrLang;
        }

        var providerOpt = options.FirstOrDefault(x => x.Key == OptionTranslatorProvider);
        if (providerOpt != null
            && Enum.IsDefined(typeof(TranslatorProvider), providerOpt.ComboBoxValue))
        {
            _settings.TranslatorProvider = (TranslatorProvider)providerOpt.ComboBoxValue;
        }

        _settings.TargetLanguage = options.FirstOrDefault(x => x.Key == OptionTargetLanguage)?.TextValue
            ?? _settings.TargetLanguage;
        if (string.IsNullOrWhiteSpace(_settings.TargetLanguage))
        {
            _settings.TargetLanguage = "简体中文";
        }

        _settings.UiLanguage = UiLanguageHelper.Normalize(
            options.FirstOrDefault(x => x.Key == OptionUiLanguage)?.TextValue ?? _settings.UiLanguage);
        if (_settings.UiLanguage == UiLanguageHelper.System)
        {
            _settings.UiLanguage = string.Empty;
        }

        _settings.HttpBaseUrl = options.FirstOrDefault(x => x.Key == OptionHttpBaseUrl)?.TextValue
            ?? _settings.HttpBaseUrl;
        var storedApiKey = options.FirstOrDefault(x => x.Key == OptionHttpApiKey)?.TextValue ?? string.Empty;
        if (string.IsNullOrWhiteSpace(storedApiKey))
        {
            // PowerToys AdditionalOptions textbox is always shown empty (placeholder only).
            // Empty on save means "keep the in-memory / previously loaded key", then re-Persist
            // Protect() so PT cannot wipe settings.json with a blank TextValue.
            if (!string.IsNullOrEmpty(_settings.HttpApiKey))
            {
                EnsureProtectedApiKeyPersisted(_settings.HttpApiKey);
            }
        }
        else
        {
            _settings.HttpApiKey = SecretProtector.Unprotect(storedApiKey);
            if (SecretProtector.NeedsMigration(storedApiKey))
            {
                MigrateApiKeyToProtected(_settings.HttpApiKey);
            }
            else if (!SecretProtector.IsProtected(storedApiKey))
            {
                // Fresh plaintext typed in AdditionalOptions → encrypt to disk.
                MigrateApiKeyToProtected(_settings.HttpApiKey);
            }
        }

        _settings.HttpModel = options.FirstOrDefault(x => x.Key == OptionHttpModel)?.TextValue
            ?? _settings.HttpModel;
        _settings.HyMtGgufPath = options.FirstOrDefault(x => x.Key == OptionHyMtGgufPath)?.TextValue ?? string.Empty;
        _settings.HyMtCustomDownloadUrl = options.FirstOrDefault(x => x.Key == OptionHyMtCustomDownloadUrl)?.TextValue
            ?? string.Empty;

        var backendOpt = options.FirstOrDefault(x => x.Key == OptionHyMtBackend);
        if (backendOpt != null
            && Enum.IsDefined(typeof(HyMtBackendKind), backendOpt.ComboBoxValue))
        {
            _settings.HyMtBackend = (HyMtBackendKind)backendOpt.ComboBoxValue;
        }

        // Action-like checkbox: download recommended GGUF, fill path, then clear the check.
        var downloadOpt = options.FirstOrDefault(x => x.Key == OptionDownloadHyMt);
        if (downloadOpt != null && downloadOpt.Value)
        {
            downloadOpt.Value = false;
            TryDownloadRecommendedModel(options);
        }

        // Prefer an existing LocalAppData cache when the path textbox is empty.
        if (string.IsNullOrWhiteSpace(_settings.HyMtGgufPath)
            && HyMtModelDownloader.IsValidCachedFile(HyMtModelDownloader.DefaultCachePath))
        {
            ApplyGgufPath(options, HyMtModelDownloader.DefaultCachePath);
        }

        UiLanguageHelper.Apply(_settings.UiLanguage);
        ApplyHotkeyRegistration();
        UpdateChecker.MaybeStart(_settings.CheckForUpdates, UpdateChecker.DefaultReleasesApi, Notify);
    }

    private void OpenSettingsWindow()
    {
        UiLanguageHelper.Apply(_settings.UiLanguage);
        SnipController.ShowSettingsWindow(_settings, onSaved: () =>
        {
            UiLanguageHelper.Apply(_settings.UiLanguage);
            HyMT2SharpTranslatorCache.ConfigureIdleUnload(_settings.HyMtIdleUnloadMinutes);
            WeChatOcrService.ConfigureIdleRecycle(_settings.HyMtIdleUnloadMinutes);
            SnipVerbose.ScrollLogging = _settings.VerboseScrollLogging;
            ApplyHotkeyRegistration();
            UpdateChecker.MaybeStart(_settings.CheckForUpdates, UpdateChecker.DefaultReleasesApi, Notify);
        });
    }

    private void ApplyHotkeyRegistration()
    {
        // Register on the dedicated STA UI thread so the message-only HWND has a pump.
        SnipController.InvokeOnUiThread(() =>
        {
            try
            {
                _hotkeyService ??= new GlobalHotkeyService();
                if (!_hotkeyService.TryRegister(
                        _settings.GlobalHotkey,
                        () => SnipController.Start(_settings, Notify),
                        out var error))
                {
                    Notify(Properties.Resources.hotkey_register_failed, error ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"Snip hotkey register failed: {ex.Message}", ex, GetType());
                Notify(Properties.Resources.hotkey_register_failed, ex.Message);
            }
        });
    }

    private void TryDownloadRecommendedModel(IEnumerable<PluginAdditionalOption> options)
    {
        // UpdateSettings may run off the WPF UI thread; dialogs/MessageBox need the host dispatcher.
        void Run()
        {
            try
            {
                var (ok, path, error) = HyMtModelDownloadDialog.ShowDialogAndDownload(
                    customDownloadUrl: _settings.HyMtCustomDownloadUrl);
                if (ok && !string.IsNullOrWhiteSpace(path))
                {
                    ApplyGgufPath(options, path);
                    MessageBox.Show(
                        $"{Properties.Resources.tr_download_settings_ok} {path}",
                        Properties.Resources.tr_download_title,
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(error))
                {
                    MessageBox.Show(
                        error,
                        Properties.Resources.tr_download_title,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"Snip recommended Hy-MT2 download failed: {ex.Message}", ex, GetType());
                MessageBox.Show(
                    $"{Properties.Resources.tr_err_download_network} {ex.Message}",
                    Properties.Resources.tr_download_title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(Run));
            return;
        }

        Run();
    }

    private void ApplyGgufPath(IEnumerable<PluginAdditionalOption> options, string path)
    {
        _settings.HyMtGgufPath = path;
        var pathOpt = options?.FirstOrDefault(x => x.Key == OptionHyMtGgufPath);
        if (pathOpt != null)
        {
            pathOpt.TextValue = path;
        }

        // Persist into PowerToys Run settings.json (+ sidecar) so restart shows the path.
        HyMtGgufPathPersister.Persist(path);
    }

    public void ReloadData()
    {
        if (_context is null)
        {
            return;
        }

        UpdateIconPath(_context.API.GetCurrentTheme());
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            if (_context != null && _context.API != null)
            {
                _context.API.ThemeChanged -= OnThemeChanged;
            }

            if (_hotkeyService != null)
            {
                var hotkey = _hotkeyService;
                _hotkeyService = null;
                try
                {
                    SnipController.InvokeOnUiThreadSync(() => hotkey.Dispose());
                }
                catch (Exception ex)
                {
                    Log.Exception($"Snip hotkey dispose failed: {ex.Message}", ex, GetType());
                    try
                    {
                        hotkey.Dispose();
                    }
                    catch (Exception inner)
                    {
                        Log.Exception($"Snip hotkey dispose fallback failed: {inner.Message}", inner, GetType());
                    }
                }
            }

            // After hotkey HwndSource is gone: shut down snip STA Application (if we
            // created one) + dispatcher. Does not call host Application.Shutdown.
            try
            {
                SnipController.ShutdownUiThread();
            }
            catch (Exception ex)
            {
                Log.Exception($"Snip STA shutdown failed: {ex.Message}", ex, GetType());
            }

            // Release process-wide HyMT weights if loaded; otherwise process exit cleans up.
            HyMT2SharpTranslatorCache.Unload();

            // Kill the warm WeChatOCR.exe child process started by the shared OCR component.
            WeChatOcrService.ShutdownOcr();

            _disposed = true;
        }
    }

    private void Notify(string message, string secondary)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        try
        {
            _context?.API.ShowNotification(message, secondary);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip notification failed: {ex.Message}", ex, GetType());
        }
    }

    private void OnThemeChanged(Theme oldtheme, Theme newTheme)
    {
        UpdateIconPath(newTheme);
    }

    private void UpdateIconPath(Theme theme)
    {
        _iconPath = theme == Theme.Light || theme == Theme.HighContrastWhite
            ? "Images/Snip.light.png"
            : "Images/Snip.dark.png";
    }
}
