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
    private TextBox _hotkeyBox;
    private TextBlock _hotkeyStatus;
    private ComboBox _provider;
    private TextBox _targetLang;
    private StackPanel _httpPanel;
    private StackPanel _hymtPanel;
    private TextBox _httpUrl;
    private TextBox _httpKey;
    private TextBox _httpModel;
    private TextBox _hymtPath;
    private TextBox _hymtCustomUrl;
    private ComboBox _hymtBackend;

    public SnipSettingsWindow(SnipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _target = settings;
        _draft = settings.Clone();

        Title = Properties.Resources.settings_window_title;
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

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem
        {
            Header = Properties.Resources.settings_tab_general,
            Content = BuildGeneralTab(),
        });
        tabs.Items.Add(new TabItem
        {
            Header = Properties.Resources.settings_tab_hotkey,
            Content = BuildHotkeyTab(),
        });
        tabs.Items.Add(new TabItem
        {
            Header = Properties.Resources.settings_tab_translate,
            Content = BuildTranslateTab(),
        });
        tabs.Items.Add(new TabItem
        {
            Header = Properties.Resources.settings_tab_about,
            Content = BuildAboutTab(),
        });
        root.Children.Add(tabs);

        LoadDraftIntoUi();
        UpdateTranslateVisibility();
    }

    private UIElement BuildGeneralTab()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };

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
        _httpKey = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        _httpPanel.Children.Add(_httpKey);
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
            catch
            {
                // Ignore launch failures in About.
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
        _saveDir.Text = _draft.SaveDirectory ?? string.Empty;
        _copyAfter.IsChecked = _draft.CopyAfterCapture;
        _autoSave.IsChecked = _draft.AutoSaveAfterCapture;
        _delayMs.Text = _draft.CaptureDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var hotkey = _draft.GlobalHotkey ?? string.Empty;
        _hotkeyBox.Text = hotkey;
        _hotkeyStatus.Text = string.IsNullOrWhiteSpace(hotkey)
            ? Properties.Resources.settings_hotkey_disabled
            : Properties.Resources.settings_hotkey_current.Replace("{0}", hotkey, StringComparison.Ordinal);

        SelectProvider(_draft.TranslatorProvider);
        _targetLang.Text = _draft.TargetLanguage ?? string.Empty;
        _httpUrl.Text = _draft.HttpBaseUrl ?? string.Empty;
        _httpKey.Text = _draft.HttpApiKey ?? string.Empty;
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

        _draft.SaveDirectory = _saveDir.Text?.Trim() ?? string.Empty;
        _draft.CopyAfterCapture = _copyAfter.IsChecked == true;
        _draft.AutoSaveAfterCapture = _autoSave.IsChecked == true;
        _draft.CaptureDelayMs = delay;
        _draft.GlobalHotkey = HotkeyParser.Normalize(hotkeyText);
        _draft.TranslatorProvider = SelectedProvider();
        _draft.TargetLanguage = string.IsNullOrWhiteSpace(_targetLang.Text) ? "简体中文" : _targetLang.Text.Trim();
        _draft.HttpBaseUrl = _httpUrl.Text?.Trim() ?? string.Empty;
        _draft.HttpApiKey = _httpKey.Text?.Trim() ?? string.Empty;
        _draft.HttpModel = _httpModel.Text?.Trim() ?? string.Empty;
        _draft.HyMtGgufPath = _hymtPath.Text?.Trim() ?? string.Empty;
        _draft.HyMtCustomDownloadUrl = _hymtCustomUrl.Text?.Trim() ?? string.Empty;
        _draft.HyMtBackend = SelectedBackend();

        _target.CopyFrom(_draft);
        SnipSettingsPersister.Persist(SnipSettingsPersister.BuildPatchesFromSettings(_target));

        DialogResult = true;
        Close();
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
                return "1.3.0";
            }

            var jsonPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(jsonPath))
            {
                return "1.3.0";
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.TryGetProperty("Version", out var v))
            {
                return v.GetString() ?? "1.3.0";
            }
        }
        catch (Exception)
        {
        }

        return "1.3.0";
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
