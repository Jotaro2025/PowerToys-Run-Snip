// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.Win32;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Dedicated Snip settings window (General / Hotkey / Translate / About).
/// Code-built WPF to match other plugin dialogs; Translate tab folds engine fields.
/// </summary>
public sealed class SnipSettingsWindow : Window
{
    private const string GitHubUrl = "https://github.com/Jotaro2025/PowerToys-Run-Snip";

    private readonly SnipSettings _target;
    private readonly SnipSettings _draft;

    private TextBox _saveDir;
    private CheckBox _copyAfter;
    private CheckBox _autoSave;
    private TextBox _delayMs;
    private TextBox _scrollIntervalMs;
    private TextBox _scrollMaxTiles;
    private TextBox _hymtIdleUnload;
    private TextBox _hotkeyBox;
    private TextBlock _hotkeyStatus;
    private ComboBox _provider;
    private TextBox _targetLang;
    private StackPanel _httpPanel;
    private StackPanel _hymtPanel;
    private TextBox _httpUrl;
    private PasswordBox _httpKeyPassword;
    private TextBox _httpKeyPlain;
    private CheckBox _httpKeyShow;
    private TextBox _httpModel;
    private TextBox _hymtPath;
    private TextBox _hymtCustomUrl;
    private ComboBox _hymtBackend;
    private ComboBox _uiLanguage;
    private ComboBox _ocrEngine;
    private ComboBox _windowsOcrLang;
    private TabControl _tabs;
    private bool _suppressLanguageChanged;

    public SnipSettingsWindow(SnipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _target = settings;
        _draft = settings.Clone();

        Width = 520;
        MinWidth = 480;
        Height = 520;
        MinHeight = 420;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;

        BuildUi();
        LoadDraftIntoUi();
        UpdateTranslateVisibility();
    }

    /// <summary>Rebuilds chrome + tabs from current resource culture (live language switch).</summary>
    private void BuildUi()
    {
        var selectedTab = _tabs?.SelectedIndex ?? 0;

        Title = Properties.Resources.settings_window_title;

        var root = new DockPanel { Margin = new Thickness(12) };
        Content = root;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var cancel = new Button
        {
            Content = Properties.Resources.settings_cancel,
            MinWidth = 88,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true,
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };

        var save = new Button
        {
            Content = Properties.Resources.settings_save,
            MinWidth = 88,
            Height = 28,
            IsDefault = true,
        };
        save.Click += (_, _) => OnSave();

        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        _tabs = new TabControl();
        _tabs.Items.Add(new TabItem
        {
            Header = Properties.Resources.settings_tab_general,
            Content = BuildGeneralTab(),
        });
        _tabs.Items.Add(new TabItem
        {
            Header = Properties.Resources.settings_tab_hotkey,
            Content = BuildHotkeyTab(),
        });
        _tabs.Items.Add(new TabItem
        {
            Header = Properties.Resources.settings_tab_translate,
            Content = BuildTranslateTab(),
        });
        _tabs.Items.Add(new TabItem
        {
            Header = Properties.Resources.settings_tab_about,
            Content = BuildAboutTab(),
        });
        root.Children.Add(_tabs);

        if (selectedTab >= 0 && selectedTab < _tabs.Items.Count)
        {
            _tabs.SelectedIndex = selectedTab;
        }
    }

    private UIElement BuildGeneralTab()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };

        panel.Children.Add(Label(Properties.Resources.opt_ui_lang_label));
        _uiLanguage = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 4),
            MinHeight = 28,
        };
        _uiLanguage.Items.Add(new ComboBoxItem
        {
            Content = "跟随系统 / Follow system",
            Tag = UiLanguageHelper.System,
        });
        _uiLanguage.Items.Add(new ComboBoxItem
        {
            Content = "English",
            Tag = UiLanguageHelper.English,
        });
        _uiLanguage.Items.Add(new ComboBoxItem
        {
            Content = "简体中文",
            Tag = UiLanguageHelper.ChineseSimplified,
        });
        _uiLanguage.SelectionChanged += OnUiLanguageSelectionChanged;
        panel.Children.Add(_uiLanguage);
        panel.Children.Add(Hint(Properties.Resources.opt_ui_lang_desc));

        panel.Children.Add(Label(Properties.Resources.opt_save_dir_label));
        _saveDir = new TextBox { Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(_saveDir);
        panel.Children.Add(Hint(Properties.Resources.opt_save_dir_desc));

        _copyAfter = new CheckBox
        {
            Content = Properties.Resources.opt_copy_label,
            Margin = new Thickness(0, 8, 0, 4),
        };
        panel.Children.Add(_copyAfter);

        _autoSave = new CheckBox
        {
            Content = Properties.Resources.opt_autosave_label,
            Margin = new Thickness(0, 4, 0, 8),
        };
        panel.Children.Add(_autoSave);

        panel.Children.Add(Label(Properties.Resources.opt_delay_label));
        _delayMs = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(_delayMs);
        panel.Children.Add(Hint(Properties.Resources.opt_delay_desc));

        panel.Children.Add(Label(Properties.Resources.opt_scroll_interval_label));
        _scrollIntervalMs = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_scrollIntervalMs);
        panel.Children.Add(Hint(Properties.Resources.opt_scroll_interval_desc));

        panel.Children.Add(Label(Properties.Resources.opt_scroll_maxtiles_label));
        _scrollMaxTiles = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_scrollMaxTiles);
        panel.Children.Add(Hint(Properties.Resources.opt_scroll_maxtiles_desc));

        panel.Children.Add(Label(Properties.Resources.opt_ocr_engine_label));
        _ocrEngine = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 4),
            MinHeight = 28,
        };
        _ocrEngine.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_ocr_engine_auto, Tag = OcrEngineKind.Auto });
        _ocrEngine.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_ocr_engine_wechat, Tag = OcrEngineKind.WeChat });
        _ocrEngine.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_ocr_engine_windows, Tag = OcrEngineKind.Windows });
        panel.Children.Add(_ocrEngine);
        panel.Children.Add(Hint(Properties.Resources.opt_ocr_engine_desc));

        panel.Children.Add(Label(Properties.Resources.opt_windows_ocr_lang_label));
        _windowsOcrLang = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 4),
            MinHeight = 28,
        };
        _windowsOcrLang.Items.Add(new ComboBoxItem
        {
            Content = Properties.Resources.opt_windows_ocr_lang_auto,
            Tag = "auto",
        });
        foreach (var tag in WindowsOcrService.GetInstalledLanguageTags())
        {
            _windowsOcrLang.Items.Add(new ComboBoxItem { Content = tag, Tag = tag });
        }

        panel.Children.Add(_windowsOcrLang);
        panel.Children.Add(Hint(Properties.Resources.opt_windows_ocr_lang_desc));

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private UIElement BuildHotkeyTab()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(Hint(Properties.Resources.settings_hotkey_hint));

        panel.Children.Add(Label(Properties.Resources.opt_hotkey_label));
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var clear = new Button
        {
            Content = Properties.Resources.settings_hotkey_clear,
            MinWidth = 72,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(clear, Dock.Right);
        clear.Click += (_, _) =>
        {
            _hotkeyBox.Text = string.Empty;
            _hotkeyStatus.Text = Properties.Resources.settings_hotkey_disabled;
            _hotkeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108));
        };

        _hotkeyBox = new TextBox
        {
            IsReadOnly = true,
            Height = 28,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _hotkeyBox.PreviewKeyDown += OnHotkeyPreviewKeyDown;
        row.Children.Add(clear);
        row.Children.Add(_hotkeyBox);
        panel.Children.Add(row);

        _hotkeyStatus = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(_hotkeyStatus);
        panel.Children.Add(Hint(Properties.Resources.opt_hotkey_desc));

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private UIElement BuildTranslateTab()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };

        panel.Children.Add(Label(Properties.Resources.opt_tr_provider_label));
        _provider = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 8),
            MinHeight = 28,
        };
        _provider.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_tr_provider_off, Tag = TranslatorProvider.Off });
        _provider.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_tr_provider_http, Tag = TranslatorProvider.HttpOpenAi });
        _provider.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_tr_provider_hymt, Tag = TranslatorProvider.HyMT2Sharp });
        _provider.SelectionChanged += (_, _) => UpdateTranslateVisibility();
        panel.Children.Add(_provider);
        panel.Children.Add(Hint(Properties.Resources.settings_tr_fold_hint));

        panel.Children.Add(Label(Properties.Resources.opt_tr_lang_label));
        _targetLang = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(_targetLang);

        _httpPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        _httpPanel.Children.Add(SectionTitle("HTTP"));
        _httpPanel.Children.Add(Label(Properties.Resources.opt_tr_http_url_label));
        _httpUrl = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        _httpPanel.Children.Add(_httpUrl);
        _httpPanel.Children.Add(Label(Properties.Resources.opt_tr_http_key_label));
        var keyRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        _httpKeyShow = new CheckBox
        {
            Content = Properties.Resources.settings_http_key_show,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(_httpKeyShow, Dock.Right);
        keyRow.Children.Add(_httpKeyShow);
        _httpKeyPassword = new PasswordBox { Margin = new Thickness(0) };
        _httpKeyPlain = new TextBox { Margin = new Thickness(0), Visibility = Visibility.Collapsed };
        var keyHost = new Grid();
        keyHost.Children.Add(_httpKeyPassword);
        keyHost.Children.Add(_httpKeyPlain);
        keyRow.Children.Add(keyHost);
        _httpKeyShow.Checked += OnHttpKeyShowToggled;
        _httpKeyShow.Unchecked += OnHttpKeyShowToggled;
        _httpPanel.Children.Add(keyRow);
        _httpPanel.Children.Add(Label(Properties.Resources.opt_tr_http_model_label));
        _httpModel = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        _httpPanel.Children.Add(_httpModel);
        panel.Children.Add(_httpPanel);

        _hymtPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        _hymtPanel.Children.Add(SectionTitle("HyMT"));
        _hymtPanel.Children.Add(Label(Properties.Resources.opt_tr_hymt_path_label));
        var pathRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var browse = new Button
        {
            Content = Properties.Resources.settings_browse,
            MinWidth = 72,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Click += (_, _) => BrowseGguf();
        _hymtPath = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, Height = 28 };
        pathRow.Children.Add(browse);
        pathRow.Children.Add(_hymtPath);
        _hymtPanel.Children.Add(pathRow);

        var download = new Button
        {
            Content = Properties.Resources.opt_tr_hymt_download_label,
            Height = 28,
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 0, 10, 0),
        };
        download.Click += (_, _) => DownloadRecommended();
        _hymtPanel.Children.Add(download);

        _hymtPanel.Children.Add(Label(Properties.Resources.opt_tr_hymt_custom_url_label));
        _hymtCustomUrl = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        _hymtPanel.Children.Add(_hymtCustomUrl);

        _hymtPanel.Children.Add(Label(Properties.Resources.opt_tr_hymt_backend_label));
        _hymtBackend = new ComboBox { MinHeight = 28, Margin = new Thickness(0, 0, 0, 6) };
        _hymtBackend.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_tr_hymt_backend_auto, Tag = HyMtBackendKind.Auto });
        _hymtBackend.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_tr_hymt_backend_cpu, Tag = HyMtBackendKind.Cpu });
        _hymtBackend.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_tr_hymt_backend_vulkan, Tag = HyMtBackendKind.Vulkan });
        _hymtPanel.Children.Add(_hymtBackend);

        _hymtPanel.Children.Add(Label(Properties.Resources.opt_hymt_idle_unload_label));
        _hymtIdleUnload = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 2) };
        _hymtPanel.Children.Add(_hymtIdleUnload);
        _hymtPanel.Children.Add(Hint(Properties.Resources.opt_hymt_idle_unload_desc));

        var unload = new Button
        {
            Content = Properties.Resources.settings_unload_hymt,
            Height = 28,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 0, 10, 0),
        };
        unload.Click += (_, _) =>
        {
            HyMT2SharpTranslatorCache.Unload();
            MessageBox.Show(
                Properties.Resources.opt_unload_hymt_done,
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        };
        _hymtPanel.Children.Add(unload);

        panel.Children.Add(_hymtPanel);

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private UIElement BuildAboutTab()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        var version = ReadPluginVersion();

        panel.Children.Add(new TextBlock
        {
            Text = Properties.Resources.plugin_name,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{Properties.Resources.settings_about_version}: {version}",
            Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{Properties.Resources.settings_about_author}: Jotaro2025",
            Margin = new Thickness(0, 0, 0, 8),
        });

        var linkBlock = new TextBlock { Margin = new Thickness(0, 0, 0, 12) };
        var link = new Hyperlink(new System.Windows.Documents.Run(GitHubUrl)) { NavigateUri = new Uri(GitHubUrl) };
        link.RequestNavigate += (_, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // Ignore launch failures in About (logged for diagnostics).
                Log.Exception($"Snip settings: open GitHub link failed: {ex.Message}", ex, typeof(SnipSettingsWindow));
            }

            e.Handled = true;
        };
        linkBlock.Inlines.Add(new System.Windows.Documents.Run(Properties.Resources.settings_about_github + ": "));
        linkBlock.Inlines.Add(link);
        panel.Children.Add(linkBlock);

        panel.Children.Add(new TextBlock
        {
            Text = Properties.Resources.settings_about_ocr_note,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 88)),
        });

        return panel;
    }

    private void LoadDraftIntoUi()
    {
        SelectUiLanguage(_draft.UiLanguage);
        _saveDir.Text = _draft.SaveDirectory ?? string.Empty;
        _copyAfter.IsChecked = _draft.CopyAfterCapture;
        _autoSave.IsChecked = _draft.AutoSaveAfterCapture;
        _delayMs.Text = _draft.CaptureDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _scrollIntervalMs.Text = _draft.ScrollIntervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _scrollMaxTiles.Text = _draft.ScrollMaxTiles.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _hymtIdleUnload.Text = _draft.HyMtIdleUnloadMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var hotkey = _draft.GlobalHotkey ?? string.Empty;
        _hotkeyBox.Text = hotkey;
        _hotkeyStatus.Text = string.IsNullOrWhiteSpace(hotkey)
            ? Properties.Resources.settings_hotkey_disabled
            : Properties.Resources.settings_hotkey_current.Replace("{0}", hotkey, StringComparison.Ordinal);

        SelectOcrEngine(_draft.OcrEngine);
        SelectWindowsOcrLanguage(_draft.WindowsOcrLanguage);
        SelectProvider(_draft.TranslatorProvider);
        _targetLang.Text = _draft.TargetLanguage ?? string.Empty;
        _httpUrl.Text = _draft.HttpBaseUrl ?? string.Empty;
        var key = _draft.HttpApiKey ?? string.Empty;
        _httpKeyPassword.Password = key;
        _httpKeyPlain.Text = key;
        _httpKeyShow.IsChecked = false;
        _httpKeyPassword.Visibility = Visibility.Visible;
        _httpKeyPlain.Visibility = Visibility.Collapsed;
        _httpModel.Text = _draft.HttpModel ?? string.Empty;
        _hymtPath.Text = _draft.HyMtGgufPath ?? string.Empty;
        _hymtCustomUrl.Text = _draft.HyMtCustomDownloadUrl ?? string.Empty;
        SelectBackend(_draft.HyMtBackend);
    }

    private void OnSave()
    {
        if (!int.TryParse(_delayMs.Text?.Trim(), out var delay) || delay < 0)
        {
            delay = 150;
        }

        delay = Math.Min(2000, delay);

        var hotkeyText = (_hotkeyBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(hotkeyText)
            && !HotkeyParser.TryParse(hotkeyText, out _, out _, out var parseError))
        {
            MessageBox.Show(
                parseError ?? Properties.Resources.settings_hotkey_invalid,
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        CaptureDraftFromUi(delay, hotkeyText);

        _target.CopyFrom(_draft);
        UiLanguageHelper.Apply(_target.UiLanguage);
        SnipSettingsPersister.Persist(SnipSettingsPersister.BuildPatchesFromSettings(_target));

        DialogResult = true;
        Close();
    }

    private void CaptureDraftFromUi(int delay, string hotkeyText)
    {
        _draft.SaveDirectory = _saveDir.Text?.Trim() ?? string.Empty;
        _draft.CopyAfterCapture = _copyAfter.IsChecked == true;
        _draft.AutoSaveAfterCapture = _autoSave.IsChecked == true;
        _draft.CaptureDelayMs = delay;
        if (!int.TryParse(_scrollIntervalMs.Text?.Trim(), out var scrollInterval))
        {
            scrollInterval = _draft.ScrollIntervalMs;
        }

        _draft.ScrollIntervalMs = Math.Clamp(scrollInterval, 50, 1000);
        if (!int.TryParse(_scrollMaxTiles.Text?.Trim(), out var scrollMax))
        {
            scrollMax = _draft.ScrollMaxTiles;
        }

        _draft.ScrollMaxTiles = Math.Clamp(scrollMax, 10, 500);
        if (!int.TryParse(_hymtIdleUnload.Text?.Trim(), out var idleUnload))
        {
            idleUnload = _draft.HyMtIdleUnloadMinutes;
        }

        _draft.HyMtIdleUnloadMinutes = Math.Clamp(idleUnload, 0, HyMT2SharpTranslatorCache.MaxIdleUnloadMinutes);
        _draft.GlobalHotkey = HotkeyParser.Normalize(hotkeyText);
        _draft.OcrEngine = SelectedOcrEngine();
        _draft.WindowsOcrLanguage = SelectedWindowsOcrLanguage();
        _draft.TranslatorProvider = SelectedProvider();
        _draft.TargetLanguage = string.IsNullOrWhiteSpace(_targetLang.Text) ? "简体中文" : _targetLang.Text.Trim();
        var uiLang = SelectedUiLanguage();
        _draft.UiLanguage = uiLang == UiLanguageHelper.System ? string.Empty : uiLang;
        _draft.HttpBaseUrl = _httpUrl.Text?.Trim() ?? string.Empty;
        _draft.HttpApiKey = GetHttpApiKeyFromUi();
        _draft.HttpModel = _httpModel.Text?.Trim() ?? string.Empty;
        _draft.HyMtGgufPath = _hymtPath.Text?.Trim() ?? string.Empty;
        _draft.HyMtCustomDownloadUrl = _hymtCustomUrl.Text?.Trim() ?? string.Empty;
        _draft.HyMtBackend = SelectedBackend();
    }

    private void OnUiLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageChanged || _uiLanguage == null)
        {
            return;
        }

        var next = SelectedUiLanguage();
        var current = UiLanguageHelper.Normalize(_draft.UiLanguage);
        if (string.Equals(next, current, StringComparison.OrdinalIgnoreCase)
            || (next == UiLanguageHelper.System && string.IsNullOrWhiteSpace(_draft.UiLanguage)))
        {
            _draft.UiLanguage = next == UiLanguageHelper.System ? string.Empty : next;
            return;
        }

        // Keep other edits; apply culture and rebuild labels immediately.
        if (!int.TryParse(_delayMs.Text?.Trim(), out var delay) || delay < 0)
        {
            delay = _draft.CaptureDelayMs;
        }

        delay = Math.Min(2000, delay);
        CaptureDraftFromUi(delay, (_hotkeyBox.Text ?? string.Empty).Trim());
        _draft.UiLanguage = next == UiLanguageHelper.System ? string.Empty : next;
        UiLanguageHelper.Apply(_draft.UiLanguage);

        _suppressLanguageChanged = true;
        try
        {
            BuildUi();
            LoadDraftIntoUi();
            UpdateTranslateVisibility();
        }
        finally
        {
            _suppressLanguageChanged = false;
        }
    }

    private void SelectUiLanguage(string uiLanguage)
    {
        var key = UiLanguageHelper.Normalize(uiLanguage);
        var wasSuppressed = _suppressLanguageChanged;
        _suppressLanguageChanged = true;
        try
        {
            for (var i = 0; i < _uiLanguage.Items.Count; i++)
            {
                if (_uiLanguage.Items[i] is ComboBoxItem item
                    && item.Tag is string tag
                    && string.Equals(tag, key, StringComparison.OrdinalIgnoreCase))
                {
                    _uiLanguage.SelectedIndex = i;
                    return;
                }
            }

            _uiLanguage.SelectedIndex = 0;
        }
        finally
        {
            _suppressLanguageChanged = wasSuppressed;
        }
    }

    private string SelectedUiLanguage()
    {
        if (_uiLanguage?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return UiLanguageHelper.Normalize(tag);
        }

        return UiLanguageHelper.System;
    }

    private void OnHotkeyPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HotkeyParser.IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            e.Handled = true;
            return;
        }

        var mods = HotkeyParser.ModifiersFromKeyboard();
        if (mods == 0)
        {
            _hotkeyStatus.Text = Properties.Resources.settings_hotkey_need_modifier;
            _hotkeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(180, 80, 60));
            e.Handled = true;
            return;
        }

        var display = HotkeyParser.Format(mods, key);
        _hotkeyBox.Text = display;
        _hotkeyStatus.Text = Properties.Resources.settings_hotkey_current.Replace("{0}", display, StringComparison.Ordinal);
        _hotkeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108));
        e.Handled = true;
    }

    private void UpdateTranslateVisibility()
    {
        var provider = SelectedProvider();
        _httpPanel.Visibility = provider == TranslatorProvider.HttpOpenAi
            ? Visibility.Visible
            : Visibility.Collapsed;
        _hymtPanel.Visibility = provider == TranslatorProvider.HyMT2Sharp
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnHttpKeyShowToggled(object sender, RoutedEventArgs e)
    {
        bool show = _httpKeyShow.IsChecked == true;
        if (show)
        {
            _httpKeyPlain.Text = _httpKeyPassword.Password ?? string.Empty;
            _httpKeyPassword.Visibility = Visibility.Collapsed;
            _httpKeyPlain.Visibility = Visibility.Visible;
            _httpKeyShow.Content = Properties.Resources.settings_http_key_hide;
        }
        else
        {
            _httpKeyPassword.Password = _httpKeyPlain.Text ?? string.Empty;
            _httpKeyPlain.Visibility = Visibility.Collapsed;
            _httpKeyPassword.Visibility = Visibility.Visible;
            _httpKeyShow.Content = Properties.Resources.settings_http_key_show;
        }
    }

    private string GetHttpApiKeyFromUi()
    {
        if (_httpKeyShow?.IsChecked == true)
        {
            return _httpKeyPlain?.Text?.Trim() ?? string.Empty;
        }

        return _httpKeyPassword?.Password?.Trim() ?? string.Empty;
    }

    private void SelectWindowsOcrLanguage(string tag)
    {
        var want = string.IsNullOrWhiteSpace(tag) ? "auto" : tag.Trim();
        for (int i = 0; i < _windowsOcrLang.Items.Count; i++)
        {
            if (_windowsOcrLang.Items[i] is ComboBoxItem item
                && item.Tag is string t
                && t.Equals(want, StringComparison.OrdinalIgnoreCase))
            {
                _windowsOcrLang.SelectedIndex = i;
                return;
            }
        }

        // Saved tag not installed anymore — keep Auto, but surface the raw tag as an extra item.
        if (!want.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            _windowsOcrLang.Items.Add(new ComboBoxItem { Content = want + " (?)", Tag = want });
            _windowsOcrLang.SelectedIndex = _windowsOcrLang.Items.Count - 1;
            return;
        }

        _windowsOcrLang.SelectedIndex = 0;
    }

    private string SelectedWindowsOcrLanguage()
        => _windowsOcrLang?.SelectedItem is ComboBoxItem item && item.Tag is string t && !string.IsNullOrWhiteSpace(t)
            ? t
            : "auto";

    private void SelectOcrEngine(OcrEngineKind engine)
    {
        _ocrEngine.SelectedIndex = 0;
        for (var i = 0; i < _ocrEngine.Items.Count; i++)
        {
            if (_ocrEngine.Items[i] is ComboBoxItem item && item.Tag is OcrEngineKind k && k == engine)
            {
                _ocrEngine.SelectedIndex = i;
                return;
            }
        }
    }

    private OcrEngineKind SelectedOcrEngine()
        => _ocrEngine?.SelectedItem is ComboBoxItem item && item.Tag is OcrEngineKind k ? k : OcrEngineKind.Auto;

    private void SelectProvider(TranslatorProvider provider)
    {
        for (var i = 0; i < _provider.Items.Count; i++)
        {
            if (_provider.Items[i] is ComboBoxItem item && item.Tag is TranslatorProvider p && p == provider)
            {
                _provider.SelectedIndex = i;
                return;
            }
        }

        _provider.SelectedIndex = 0;
    }

    private TranslatorProvider SelectedProvider()
    {
        if (_provider.SelectedItem is ComboBoxItem item && item.Tag is TranslatorProvider p)
        {
            return p;
        }

        return TranslatorProvider.Off;
    }

    private void SelectBackend(HyMtBackendKind backend)
    {
        for (var i = 0; i < _hymtBackend.Items.Count; i++)
        {
            if (_hymtBackend.Items[i] is ComboBoxItem item && item.Tag is HyMtBackendKind b && b == backend)
            {
                _hymtBackend.SelectedIndex = i;
                return;
            }
        }

        _hymtBackend.SelectedIndex = 0;
    }

    private HyMtBackendKind SelectedBackend()
    {
        if (_hymtBackend.SelectedItem is ComboBoxItem item && item.Tag is HyMtBackendKind b)
        {
            return b;
        }

        return HyMtBackendKind.Auto;
    }

    private void BrowseGguf()
    {
        var dialog = new OpenFileDialog
        {
            Title = Properties.Resources.opt_tr_hymt_path_label,
            Filter = "GGUF (*.gguf)|*.gguf|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(_hymtPath.Text))
        {
            try
            {
                dialog.InitialDirectory = Path.GetDirectoryName(_hymtPath.Text);
                dialog.FileName = Path.GetFileName(_hymtPath.Text);
            }
            catch (ArgumentException)
            {
                // Malformed path text: dialog just opens without an initial dir.
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            _hymtPath.Text = dialog.FileName;
        }
    }

    private void DownloadRecommended()
    {
        var custom = _hymtCustomUrl.Text?.Trim() ?? string.Empty;
        var (ok, path, error) = HyMtModelDownloadDialog.ShowDialogAndDownload(
            customDownloadUrl: custom,
            owner: this);
        if (ok && !string.IsNullOrWhiteSpace(path))
        {
            _hymtPath.Text = path;
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

    private static string ReadPluginVersion()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(SnipSettingsWindow).Assembly.Location);
            if (string.IsNullOrEmpty(dir))
            {
                return "1.4.0";
            }

            var jsonPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(jsonPath))
            {
                return "1.4.0";
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.TryGetProperty("Version", out var v))
            {
                return v.GetString() ?? "1.4.0";
            }
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip settings: read plugin.json version failed: {ex.Message}", ex, typeof(SnipSettingsWindow));
        }

        return "1.4.0";
    }

    private static TextBlock Label(string text)
        => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        };

    private static TextBlock Hint(string text)
        => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
        };

    private static TextBlock SectionTitle(string text)
        => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(40, 40, 48)),
        };
}
