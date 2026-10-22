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
    private const string OptionGlobalHotkey = "GlobalHotkey";
    private const string OptionTranslatorProvider = "TranslatorProvider";
    private const string OptionTargetLanguage = "TargetLanguage";
    private const string OptionHttpBaseUrl = "HttpBaseUrl";
    private const string OptionHttpApiKey = "HttpApiKey";
    private const string OptionHttpModel = "HttpModel";
    private const string OptionHyMtGgufPath = "HyMtGgufPath";
    private const string OptionHyMtBackend = "HyMtBackend";
    private const string OptionDownloadHyMt = "DownloadRecommendedHyMtModel";
    private const string OptionHyMtCustomDownloadUrl = "HyMtCustomDownloadUrl";

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
            Key = OptionGlobalHotkey,
            DisplayLabel = Properties.Resources.opt_hotkey_label,
            DisplayDescription = Properties.Resources.opt_hotkey_desc,
            PluginOptionType = PluginAdditionalOption.AdditionalOptionType.Textbox,
            TextValue = _settings.GlobalHotkey,
            PlaceholderText = Properties.Resources.opt_hotkey_placeholder,
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
            TextValue = _settings.HttpApiKey,
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
    };

    public List<Result> Query(Query query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var commands = new List<Result>
        {
            CreateRegionCommand(
                Properties.Resources.cmd_region_title,
                Properties.Resources.cmd_region_subtitle),
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

    private Result CreateRegionCommand(string title, string subtitle)
    {
        return new Result
        {
            Title = title,
            SubTitle = subtitle,
            IcoPath = _iconPath,
            QueryTextDisplay = title,
            Action = _ =>
            {
                SnipController.Start(_settings, Notify);
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
        ApplyHotkeyRegistration();
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
            _settings.CaptureDelayMs = (int)Math.Min(2000, delay.Value);
        }

        var hotkey = options.FirstOrDefault(x => x.Key == OptionGlobalHotkey)?.TextValue;
        _settings.GlobalHotkey = hotkey != null ? HotkeyParser.Normalize(hotkey) : string.Empty;

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

        _settings.HttpBaseUrl = options.FirstOrDefault(x => x.Key == OptionHttpBaseUrl)?.TextValue
            ?? _settings.HttpBaseUrl;
        _settings.HttpApiKey = options.FirstOrDefault(x => x.Key == OptionHttpApiKey)?.TextValue ?? string.Empty;
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

        ApplyHotkeyRegistration();
    }

    private void OpenSettingsWindow()
    {
        SnipController.ShowSettingsWindow(_settings, onSaved: () =>
        {
            ApplyHotkeyRegistration();
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

            // Release process-wide HyMT weights if loaded; otherwise process exit cleans up.
            HyMT2SharpTranslatorCache.Unload();

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
