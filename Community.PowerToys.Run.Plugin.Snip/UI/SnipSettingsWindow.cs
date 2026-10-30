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
using System.Windows.Markup;
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
    private Border _httpPanel;
    private Border _hymtPanel;
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

        Width = 640;
        MinWidth = 560;
        Height = 680;
        MinHeight = 500;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        BuildUi();
        LoadDraftIntoUi();
        UpdateTranslateVisibility();
    }

    /// <summary>Rebuilds chrome + tabs from current resource culture (live language switch).</summary>
    private void BuildUi()
    {
        var selectedTab = _tabs?.SelectedIndex ?? 0;

        Title = Properties.Resources.settings_window_title;

        var root = new DockPanel { LastChildFill = true };
        Content = root;

        // Footer: hairline separator + right-aligned actions.
        var footer = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEA)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 12, 16, 14),
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var cancel = SecondaryButton(Properties.Resources.settings_cancel);
        cancel.MinWidth = 96;
        cancel.Height = 32;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.IsCancel = true;
        cancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };

        var save = PrimaryButton(Properties.Resources.settings_save);
        save.MinWidth = 96;
        save.Height = 32;
        save.IsDefault = true;
        save.Click += (_, _) => OnSave();

        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        footer.Child = buttons;
        root.Children.Add(footer);

        _tabs = new TabControl
        {
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.White,
            Margin = new Thickness(6, 6, 6, 0),
        };
        _tabs.Items.Add(CreateTab(Properties.Resources.settings_tab_general, BuildGeneralTab()));
        _tabs.Items.Add(CreateTab(Properties.Resources.settings_tab_hotkey, BuildHotkeyTab()));
        _tabs.Items.Add(CreateTab(Properties.Resources.settings_tab_translate, BuildTranslateTab()));
        _tabs.Items.Add(CreateTab(Properties.Resources.settings_tab_about, BuildAboutTab()));
        root.Children.Add(_tabs);

        if (selectedTab >= 0 && selectedTab < _tabs.Items.Count)
        {
            _tabs.SelectedIndex = selectedTab;
        }
    }

    private static TabItem CreateTab(string header, UIElement content)
        => new TabItem
        {
            Header = header,
            Content = content,
            Padding = new Thickness(14, 7, 14, 7),
            FontSize = 13,
        };

    private static UIElement TabPage(params UIElement[] cards)
    {
        var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
        foreach (var card in cards)
        {
            panel.Children.Add(card);
        }

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private static Border Card(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE9)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 4),
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack,
        };
    }

    private static TextBlock SectionHeader(string title)
        => new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x66)),
            Margin = new Thickness(0, 0, 0, 10),
        };

    /// <summary>Label + control + optional hint, with uniform spacing.</summary>
    private static UIElement Field(string label, UIElement control, string hint = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (!string.IsNullOrEmpty(label))
        {
            stack.Children.Add(Label(label));
        }

        stack.Children.Add(control);
        if (!string.IsNullOrEmpty(hint))
        {
            stack.Children.Add(Hint(hint));
        }

        return stack;
    }

    private static TextBox CreateTextBox(string text = null, bool readOnly = false)
        => new TextBox
        {
            Text = text ?? string.Empty,
            IsReadOnly = readOnly,
            MinHeight = 30,
            Padding = new Thickness(8, 5, 8, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xDA)),
        };

    private static TextBox NumberBox()
    {
        var box = CreateTextBox();
        box.Width = 110;
        box.HorizontalAlignment = HorizontalAlignment.Left;
        return box;
    }

    private static PasswordBox CreatePasswordBox()
        => new PasswordBox
        {
            MinHeight = 30,
            Padding = new Thickness(8, 5, 8, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xDA)),
        };

    private static ComboBox CreateCombo()
        => new ComboBox
        {
            MinHeight = 30,
            Padding = new Thickness(8, 4, 6, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

    private static Button PrimaryButton(string text)
        => new Button
        {
            Content = text,
            Height = 30,
            MinWidth = 88,
            Padding = new Thickness(14, 0, 14, 0),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD)),
            BorderThickness = new Thickness(1),
            Template = RoundedButtonTemplate(),
        };

    private static Button SecondaryButton(string text)
        => new Button
        {
            Content = text,
            Height = 30,
            MinWidth = 76,
            Padding = new Thickness(12, 0, 12, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x31)),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xDA)),
            BorderThickness = new Thickness(1),
            Template = RoundedButtonTemplate(),
        };

    private static ControlTemplate RoundedButtonTemplate()
    {
        const string xaml =
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'"
            + " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>"
            + "<Border x:Name='chrome' CornerRadius='5' Background='{TemplateBinding Background}'"
            + " BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'"
            + " Padding='{TemplateBinding Padding}'>"
            + "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>"
            + "</Border>"
            + "<ControlTemplate.Triggers>"
            + "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='chrome' Property='Opacity' Value='0.86'/></Trigger>"
            + "<Trigger Property='IsPressed' Value='True'><Setter TargetName='chrome' Property='Opacity' Value='0.72'/></Trigger>"
            + "<Trigger Property='IsEnabled' Value='False'><Setter TargetName='chrome' Property='Opacity' Value='0.45'/></Trigger>"
            + "</ControlTemplate.Triggers>"
            + "</ControlTemplate>";

        return (ControlTemplate)XamlReader.Parse(xaml);
    }

    private UIElement BuildGeneralTab()
    {
        _uiLanguage = CreateCombo();
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

        _saveDir = CreateTextBox();
        _copyAfter = new CheckBox
        {
            Content = Properties.Resources.opt_copy_label,
            Margin = new Thickness(0, 0, 0, 8),
        };
        _autoSave = new CheckBox
        {
            Content = Properties.Resources.opt_autosave_label,
            Margin = new Thickness(0, 0, 0, 8),
        };
        _delayMs = NumberBox();
        _scrollIntervalMs = NumberBox();
        _scrollMaxTiles = NumberBox();

        _ocrEngine = CreateCombo();
        _ocrEngine.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_ocr_engine_auto, Tag = OcrEngineKind.Auto });
        _ocrEngine.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_ocr_engine_wechat, Tag = OcrEngineKind.WeChat });
        _ocrEngine.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_ocr_engine_windows, Tag = OcrEngineKind.Windows });
        _ocrEngine.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_ocr_engine_paddle, Tag = OcrEngineKind.SimdPaddle });

        _windowsOcrLang = CreateCombo();
        _windowsOcrLang.Items.Add(new ComboBoxItem
        {
            Content = Properties.Resources.opt_windows_ocr_lang_auto,
            Tag = "auto",
        });
        foreach (var tag in WindowsOcrService.GetInstalledLanguageTags())
        {
            _windowsOcrLang.Items.Add(new ComboBoxItem { Content = tag, Tag = tag });
        }

        return TabPage(
            Card(
                SectionHeader(Properties.Resources.settings_section_interface),
                Field(Properties.Resources.opt_ui_lang_label, _uiLanguage, Properties.Resources.opt_ui_lang_desc)),
            Card(
                SectionHeader(Properties.Resources.settings_section_capture),
                Field(Properties.Resources.opt_save_dir_label, _saveDir, Properties.Resources.opt_save_dir_desc),
                Field(null, _copyAfter),
                Field(null, _autoSave),
                Field(Properties.Resources.opt_delay_label, _delayMs, Properties.Resources.opt_delay_desc)),
            Card(
                SectionHeader(Properties.Resources.settings_section_scroll),
                Field(Properties.Resources.opt_scroll_interval_label, _scrollIntervalMs, Properties.Resources.opt_scroll_interval_desc),
                Field(Properties.Resources.opt_scroll_maxtiles_label, _scrollMaxTiles, Properties.Resources.opt_scroll_maxtiles_desc)),
            Card(
                SectionHeader(Properties.Resources.settings_section_ocr),
                Field(Properties.Resources.opt_ocr_engine_label, _ocrEngine, Properties.Resources.opt_ocr_engine_desc),
                Field(Properties.Resources.opt_windows_ocr_lang_label, _windowsOcrLang, Properties.Resources.opt_windows_ocr_lang_desc)));
    }

    private UIElement BuildHotkeyTab()
    {
        _hotkeyBox = CreateTextBox(readOnly: true);
        _hotkeyBox.PreviewKeyDown += OnHotkeyPreviewKeyDown;

        var clear = SecondaryButton(Properties.Resources.settings_hotkey_clear);
        clear.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(clear, Dock.Right);

        var row = new DockPanel();
        row.Children.Add(clear);
        row.Children.Add(_hotkeyBox);
        clear.Click += (_, _) =>
        {
            _hotkeyBox.Text = string.Empty;
            _hotkeyStatus.Text = Properties.Resources.settings_hotkey_disabled;
            _hotkeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108));
        };

        _hotkeyStatus = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 10),
        };

        return TabPage(
            Card(
                SectionHeader(Properties.Resources.settings_section_hotkey),
                WithBottomGap(Hint(Properties.Resources.settings_hotkey_hint)),
                Field(Properties.Resources.opt_hotkey_label, row),
                _hotkeyStatus,
                Hint(Properties.Resources.opt_hotkey_desc)));
    }

    private static TextBlock WithBottomGap(TextBlock block)
    {
        block.Margin = new Thickness(block.Margin.Left, block.Margin.Top, block.Margin.Right, 12);
        return block;
    }

    private UIElement BuildTranslateTab()
    {
        _provider = CreateCombo();
        _provider.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_tr_provider_off, Tag = TranslatorProvider.Off });
        _provider.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_tr_provider_http, Tag = TranslatorProvider.HttpOpenAi });
        _provider.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_tr_provider_hymt, Tag = TranslatorProvider.HyMT2Sharp });
        _provider.SelectionChanged += (_, _) => UpdateTranslateVisibility();

        _targetLang = CreateTextBox();
        _httpUrl = CreateTextBox();
        _httpModel = CreateTextBox();

        _httpPanel = Card(
            SectionHeader("HTTP"),
            Field(StripEnginePrefix(Properties.Resources.opt_tr_http_url_label, "HTTP"), _httpUrl),
            Field(StripEnginePrefix(Properties.Resources.opt_tr_http_key_label, "HTTP"), BuildHttpKeyRow()),
            Field(StripEnginePrefix(Properties.Resources.opt_tr_http_model_label, "HTTP"), _httpModel));

        _hymtPanel = Card(
            SectionHeader("HyMT"),
            Field(StripEnginePrefix(Properties.Resources.opt_tr_hymt_path_label, "HyMT"), BuildHyMtPathRow()),
            BuildHyMtActionsRow(),
            Field(StripEnginePrefix(Properties.Resources.opt_tr_hymt_custom_url_label, "HyMT"), _hymtCustomUrl = CreateTextBox()),
            Field(StripEnginePrefix(Properties.Resources.opt_tr_hymt_backend_label, "HyMT"), BuildBackendCombo()),
            Field(
                StripEnginePrefix(Properties.Resources.opt_hymt_idle_unload_label, "HyMT"),
                _hymtIdleUnload = NumberBox(),
                Properties.Resources.opt_hymt_idle_unload_desc),
            BuildUnloadRow());

        return TabPage(
            Card(
                SectionHeader(Properties.Resources.settings_section_translate),
                Field(StripEnginePrefix(Properties.Resources.opt_tr_provider_label, "翻译"), _provider, Properties.Resources.settings_tr_fold_hint),
                Field(StripEnginePrefix(Properties.Resources.opt_tr_lang_label, "翻译"), _targetLang)),
            _httpPanel,
            _hymtPanel);
    }

    /// <summary>
    /// Drops the «HTTP ·» / «HyMT ·» / «翻译 ·» prefixes the PowerToys settings page needs
    /// for flat lists; inside an engine card the header already says which engine it is.
    /// </summary>
    private static string StripEnginePrefix(string label, string engine)
    {
        if (string.IsNullOrEmpty(label))
        {
            return label;
        }

        string prefix = engine + " · ";
        if (label.StartsWith(prefix, StringComparison.Ordinal))
        {
            return label[prefix.Length..];
        }

        prefix = engine + " ";
        if (label.StartsWith(prefix, StringComparison.Ordinal))
        {
            return label[prefix.Length..];
        }

        return label;
    }

    private UIElement BuildHttpKeyRow()
    {
        _httpKeyShow = new CheckBox
        {
            Content = Properties.Resources.settings_http_key_show,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        DockPanel.SetDock(_httpKeyShow, Dock.Right);

        _httpKeyPassword = CreatePasswordBox();
        _httpKeyPlain = CreateTextBox();
        _httpKeyPlain.Visibility = Visibility.Collapsed;

        var keyHost = new Grid();
        keyHost.Children.Add(_httpKeyPassword);
        keyHost.Children.Add(_httpKeyPlain);

        var row = new DockPanel();
        row.Children.Add(_httpKeyShow);
        row.Children.Add(keyHost);
        _httpKeyShow.Checked += OnHttpKeyShowToggled;
        _httpKeyShow.Unchecked += OnHttpKeyShowToggled;
        return row;
    }

    private UIElement BuildHyMtPathRow()
    {
        var browse = SecondaryButton(Properties.Resources.settings_browse);
        browse.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);
        browse.Click += (_, _) => BrowseGguf();

        _hymtPath = CreateTextBox();

        var row = new DockPanel();
        row.Children.Add(browse);
        row.Children.Add(_hymtPath);
        return row;
    }

    private UIElement BuildHyMtActionsRow()
    {
        var download = SecondaryButton(StripEnginePrefix(Properties.Resources.opt_tr_hymt_download_label, "HyMT"));
        download.Margin = new Thickness(0, 0, 0, 12);
        download.HorizontalAlignment = HorizontalAlignment.Left;
        download.Click += (_, _) => DownloadRecommended();
        return download;
    }

    private ComboBox BuildBackendCombo()
    {
        _hymtBackend = CreateCombo();
        _hymtBackend.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_tr_hymt_backend_auto, Tag = HyMtBackendKind.Auto });
        _hymtBackend.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_tr_hymt_backend_cpu, Tag = HyMtBackendKind.Cpu });
        _hymtBackend.Items.Add(new ComboBoxItem { Content = Properties.Resources.opt_tr_hymt_backend_vulkan, Tag = HyMtBackendKind.Vulkan });
        return _hymtBackend;
    }

    private UIElement BuildUnloadRow()
    {
        var unload = SecondaryButton(Properties.Resources.settings_unload_hymt);
        unload.Margin = new Thickness(0, 0, 0, 12);
        unload.HorizontalAlignment = HorizontalAlignment.Left;
        unload.Click += (_, _) =>
        {
            HyMT2SharpTranslatorCache.Unload();
            MessageBox.Show(
                Properties.Resources.opt_unload_hymt_done,
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        };

        return unload;
    }

    private UIElement BuildAboutTab()
    {
        var version = ReadPluginVersion();

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = Properties.Resources.plugin_name,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{Properties.Resources.settings_about_version}: {version}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108)),
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{Properties.Resources.settings_about_author}: Jotaro2025",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108)),
            Margin = new Thickness(0, 2, 0, 0),
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

        var note = new TextBlock
        {
            Text = Properties.Resources.settings_about_ocr_note,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(85, 85, 94)),
            Margin = new Thickness(0, 0, 0, 10),
        };

        return TabPage(Card(SectionHeader(Properties.Resources.settings_section_about), header, linkBlock, note));
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
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)),
            Margin = new Thickness(0, 0, 0, 5),
        };

    private static TextBlock Hint(string text)
        => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x78)),
            FontSize = 12,
            Margin = new Thickness(0, 5, 0, 0),
        };
}
