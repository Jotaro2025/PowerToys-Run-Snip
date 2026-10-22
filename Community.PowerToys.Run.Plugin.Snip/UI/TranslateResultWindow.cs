// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// WeChat-style image translate window: translated image on the left, editable
/// translation list on the right. Runs OCR → translate → erase+repaint on open.
/// Supports re-applying edited translations onto the image and saving the result.
/// </summary>
public sealed class TranslateResultWindow : Window, IDisposable
{
    private static readonly Color Accent = Color.FromRgb(72, 210, 110);
    private static readonly Color ErrorColor = Color.FromRgb(214, 48, 49);

    private readonly byte[] _png;
    private readonly BitmapSource _originalImage;
    private readonly SnipSettings _settings;
    private readonly Image _imageView;
    private readonly StackPanel _linesPanel;
    private readonly ScrollViewer _linesScroll;
    private readonly Border _overlay;
    private readonly ProgressBar _progress;
    private readonly TextBlock _overlayText;
    private readonly TextBlock _status;
    private readonly Button _copyAllButton;
    private readonly Button _copyImageButton;
    private readonly Button _reapplyButton;
    private readonly Button _saveImageButton;
    private readonly List<TextBox> _translationBoxes = new();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private bool _closed;
    private bool _disposed;
    private bool _actionBusy;
    private IReadOnlyList<OcrTextLine> _lines = Array.Empty<OcrTextLine>();

    public TranslateResultWindow(BitmapSource image, byte[] png, SnipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(png);
        ArgumentNullException.ThrowIfNull(settings);

        // Freeze so ImageTranslateRenderer.Render can safely run via Task.Run.
        if (image.IsFrozen)
        {
            _originalImage = image;
        }
        else
        {
            var clone = image.Clone();
            clone.Freeze();
            _originalImage = clone;
        }

        _png = png;
        _settings = settings;

        Title = Properties.Resources.tr_window_title;
        Width = 860;
        Height = 520;
        MinWidth = 480;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 14;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Content = root;

        _imageView = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(_imageView, BitmapScalingMode.HighQuality);
        var imageHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(242, 242, 244)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = _imageView,
        };
        Grid.SetColumn(imageHost, 0);
        root.Children.Add(imageHost);

        var textHost = new Grid();
        Grid.SetColumn(textHost, 2);
        root.Children.Add(textHost);

        _linesPanel = new StackPanel();
        _linesScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _linesPanel,
            Visibility = Visibility.Collapsed,
        };
        textHost.Children.Add(_linesScroll);

        _progress = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 4,
            Width = 160,
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = new SolidColorBrush(Accent),
        };
        _overlayText = new TextBlock
        {
            Text = Properties.Resources.tr_busy,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108)),
        };
        var overlayContent = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16),
        };
        overlayContent.Children.Add(_progress);
        overlayContent.Children.Add(_overlayText);
        _overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            Child = overlayContent,
        };
        textHost.Children.Add(_overlay);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        Grid.SetRow(bottom, 1);
        Grid.SetColumnSpan(bottom, 3);
        root.Children.Add(bottom);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);

        _reapplyButton = CreateButton(Properties.Resources.tr_reapply, isDefault: false);
        _reapplyButton.Click += async (sender, e) => await ReapplyAsync();
        buttons.Children.Add(_reapplyButton);

        _saveImageButton = CreateButton(Properties.Resources.tr_save_image, isDefault: false);
        _saveImageButton.Click += async (sender, e) => await SaveImageAsync();
        buttons.Children.Add(_saveImageButton);

        _copyImageButton = CreateButton(Properties.Resources.tr_copy_image, isDefault: false);
        _copyImageButton.Click += (sender, e) => CopyImage();
        buttons.Children.Add(_copyImageButton);

        _copyAllButton = CreateButton(Properties.Resources.tr_copy_all, isDefault: true);
        _copyAllButton.Click += (sender, e) => CopyText(JoinTranslations());
        buttons.Children.Add(_copyAllButton);

        var closeButton = CreateButton(Properties.Resources.ocr_close, isDefault: false);
        closeButton.IsCancel = true;
        closeButton.Click += (sender, e) => Close();
        buttons.Children.Add(closeButton);

        _status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108)),
        };
        bottom.Children.Add(_status);

        UpdateActionButtons(enabled: false);

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += async (sender, e) => await RunPipelineAsync();
        Closed += OnClosed;
    }

    private static Button CreateButton(string text, bool isDefault)
    {
        return new Button
        {
            Content = text,
            MinWidth = 84,
            Height = 30,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            IsDefault = isDefault,
        };
    }

    private async Task RunPipelineAsync()
    {
        SetBusy(WeChatOcrService.IsBusy
            ? Properties.Resources.tr_busy_ocr_wait
            : Properties.Resources.tr_busy_ocr);
        ITranslator translator = null;
        try
        {
            _lines = await WeChatOcrService.RecognizeLinesAsync(_png, _cts.Token);
            if (_closed)
            {
                return;
            }

            if (_lines.Count == 0)
            {
                ShowMessage(Properties.Resources.ocr_no_text, isError: false);
                return;
            }

            SetBusy(Properties.Resources.tr_busy_translate);
            translator = TranslatorFactory.Create(_settings);
            if (translator is HyMT2SharpTranslator hymt)
            {
                hymt.ModelLoading += OnModelLoading;
                if (hymt.IsBusy)
                {
                    SetBusy(Properties.Resources.tr_busy_hymt_wait);
                }
            }

            var sources = _lines.Select(l => l.Text).ToList();
            var translations = await translator.TranslateAsync(sources, _settings.TargetLanguage, _cts.Token);
            if (_closed)
            {
                return;
            }

            SetBusy(Properties.Resources.tr_busy_render);
            var lines = _lines;
            var rendered = await Task.Run(
                () => ImageTranslateRenderer.Render(_originalImage, lines, translations),
                _cts.Token);
            if (_closed)
            {
                return;
            }

            _imageView.Source = rendered;
            BuildTranslationEditors(_lines, translations);
            Cursor = null;
            _overlay.Visibility = Visibility.Collapsed;
            _linesScroll.Visibility = Visibility.Visible;
            UpdateActionButtons(enabled: true);
            _status.Text = Properties.Resources.tr_status_done
                .Replace("{0}", _lines.Count.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal)
                .Replace("{1}", _settings.TargetLanguage ?? string.Empty, StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            // Closed while OCR / translate / render was in flight.
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OcrException ex)
        {
            if (!_closed)
            {
                ShowMessage(ex.Message, isError: true);
            }
        }
        catch (TranslatorException ex)
        {
            if (!_closed)
            {
                ShowMessage(ex.Message, isError: true);
            }
        }
        catch (Exception ex)
        {
            if (_closed)
            {
                return;
            }

            Log.Exception($"Snip image translate failed: {ex.Message}", ex, GetType());
            ShowMessage($"{Properties.Resources.tr_err_failed} {ex.Message}", isError: true);
        }
        finally
        {
            // HyMT2Sharp is process-cached (HyMT2SharpTranslatorCache); only dispose short-lived translators.
            if (translator is not HyMT2SharpTranslator && translator is IDisposable d)
            {
                d.Dispose();
            }
        }
    }

    private void BuildTranslationEditors(IReadOnlyList<OcrTextLine> lines, IReadOnlyList<string> translations)
    {
        _linesPanel.Children.Clear();
        _translationBoxes.Clear();

        for (int i = 0; i < lines.Count; i++)
        {
            var card = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 224)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 8),
                Background = Brushes.White,
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = lines[i].Text,
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 128)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });

            var box = new TextBox
            {
                Text = translations[i] ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MinHeight = 28,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
            };
            _translationBoxes.Add(box);
            stack.Children.Add(box);

            var copyLine = new Button
            {
                Content = Properties.Resources.ocr_copy,
                Height = 26,
                MinWidth = 56,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0),
                Tag = box,
            };
            copyLine.Click += (sender, e) =>
            {
                if (sender is Button b && b.Tag is TextBox tb)
                {
                    CopyText(tb.Text);
                }
            };
            stack.Children.Add(copyLine);

            card.Child = stack;
            _linesPanel.Children.Add(card);
        }
    }

    private string JoinTranslations()
        => string.Join(Environment.NewLine, CollectTranslations());

    private void OnModelLoading(object sender, EventArgs e)
    {
        // Never block the loader thread with Dispatcher.Invoke (UI may be awaiting TranslateAsync).
        _ = Dispatcher.BeginInvoke(new Action(() => SetBusy(Properties.Resources.tr_busy_loading_model)));
    }

    private void SetBusy(string message)
    {
        _overlay.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Visible;
        _overlayText.Text = message;
        _overlayText.Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108));
        _linesScroll.Visibility = Visibility.Collapsed;
        Cursor = Cursors.AppStarting;
        UpdateActionButtons(enabled: false);
    }

    private void ShowMessage(string message, bool isError)
    {
        if (_closed)
        {
            return;
        }

        Cursor = null;
        _progress.Visibility = Visibility.Collapsed;
        _overlay.Visibility = Visibility.Visible;
        _overlayText.Text = message;
        _overlayText.Foreground = new SolidColorBrush(isError ? ErrorColor : Color.FromRgb(100, 100, 108));
        UpdateActionButtons(enabled: false);
    }

    private void UpdateActionButtons(bool enabled)
    {
        if (_copyAllButton == null || _copyImageButton == null
            || _reapplyButton == null || _saveImageButton == null)
        {
            return;
        }

        _reapplyButton.IsEnabled = enabled;
        _saveImageButton.IsEnabled = enabled;
        _copyAllButton.IsEnabled = enabled;
        _copyImageButton.IsEnabled = enabled;
    }

    private async Task ReapplyAsync()
    {
        if (_closed || _actionBusy || _lines.Count == 0 || _translationBoxes.Count != _lines.Count)
        {
            return;
        }

        _actionBusy = true;
        UpdateActionButtons(enabled: false);
        Cursor = Cursors.AppStarting;
        _status.Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108));
        _status.Text = Properties.Resources.tr_busy_rerender;

        try
        {
            var translations = CollectTranslations();
            var rendered = await Task.Run(() => ImageTranslateRenderer.Render(_originalImage, _lines, translations));
            if (_closed)
            {
                return;
            }

            _imageView.Source = rendered;
            _status.Foreground = new SolidColorBrush(Color.FromRgb(48, 175, 85));
            _status.Text = Properties.Resources.tr_status_reapplied;
            UpdateActionButtons(enabled: true);
            Cursor = null;
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip translate re-apply failed: {ex.Message}", ex, GetType());
            if (!_closed)
            {
                Cursor = null;
                _status.Foreground = new SolidColorBrush(ErrorColor);
                _status.Text = $"{Properties.Resources.tr_err_rerender} {ex.Message}";
                UpdateActionButtons(enabled: true);
            }
        }
        finally
        {
            _actionBusy = false;
        }
    }

    private async Task SaveImageAsync()
    {
        if (_closed || _actionBusy || _imageView.Source is not BitmapSource bmp)
        {
            return;
        }

        _actionBusy = true;
        UpdateActionButtons(enabled: false);
        try
        {
            var initialDir = ScreenshotFileSaver.ResolveDirectory(
                _settings.SaveDirectory, SnipSettings.DefaultSaveFolder);
            try
            {
                if (!string.IsNullOrWhiteSpace(initialDir) && !Directory.Exists(initialDir))
                {
                    Directory.CreateDirectory(initialDir);
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"Snip translate save dir create failed: {ex.Message}", ex, GetType());
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Properties.Resources.tr_save_dialog_title,
                DefaultExt = ".png",
                FileName = ScreenshotFileSaver.BuildTranslatedFileName(),
                Filter = Properties.Resources.save_dialog_filter,
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
            {
                dialog.InitialDirectory = initialDir;
            }

            if (dialog.ShowDialog(this) != true)
            {
                UpdateActionButtons(enabled: true);
                return;
            }

            // Freeze on UI, encode straight to the file (no Encode→byte[]→WriteAllBytes).
            await PngEncoder.EncodeToFileAsync(bmp, dialog.FileName);
            if (_closed)
            {
                return;
            }

            _status.Foreground = new SolidColorBrush(Color.FromRgb(48, 175, 85));
            _status.Text = $"{Properties.Resources.tr_image_saved} {dialog.FileName}";
            UpdateActionButtons(enabled: true);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip translate save image failed: {ex.Message}", ex, GetType());
            if (!_closed)
            {
                _status.Foreground = new SolidColorBrush(ErrorColor);
                _status.Text = $"{Properties.Resources.tr_err_save_image} {ex.Message}";
                UpdateActionButtons(enabled: true);
            }
        }
        finally
        {
            _actionBusy = false;
        }
    }

    private IReadOnlyList<string> CollectTranslations()
        => _translationBoxes.Select(b => b.Text ?? string.Empty).ToList();

    private void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
            _status.Foreground = new SolidColorBrush(Color.FromRgb(48, 175, 85));
            _status.Text = Properties.Resources.ocr_copied;
        }
        catch (ExternalException ex)
        {
            Log.Exception($"Snip translate clipboard copy failed: {ex.Message}", ex, GetType());
            _status.Foreground = new SolidColorBrush(ErrorColor);
            _status.Text = Properties.Resources.ocr_copy_failed;
        }
    }

    private void CopyImage()
    {
        if (_imageView.Source is not BitmapSource bmp)
        {
            return;
        }

        try
        {
            ClipboardService.CopyBitmap(bmp);
            _status.Foreground = new SolidColorBrush(Color.FromRgb(48, 175, 85));
            _status.Text = Properties.Resources.tr_image_copied;
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip translate image copy failed: {ex.Message}", ex, GetType());
            _status.Foreground = new SolidColorBrush(ErrorColor);
            _status.Text = Properties.Resources.ocr_copy_failed;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = SaveImageAsync();
        }
    }

    private void OnClosed(object sender, EventArgs e)
    {
        _closed = true;
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
