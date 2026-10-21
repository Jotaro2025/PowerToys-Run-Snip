// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// WeChat-style "extract text" window: the captured region on the left, the
/// recognized (editable, selectable) text on the right, plus copy actions.
/// Runs the OCR when it opens and shows a busy state until the text arrives.
/// </summary>
public sealed class OcrResultWindow : Window
{
    private static readonly Color Accent = Color.FromRgb(72, 210, 110);
    private static readonly Color ErrorColor = Color.FromRgb(214, 48, 49);

    private readonly byte[] _png;
    private readonly string _weChatDir;
    private readonly TextBox _textBox;
    private readonly Border _overlay;
    private readonly ProgressBar _progress;
    private readonly TextBlock _overlayText;
    private readonly TextBlock _status;
    private readonly Button _copyButton;
    private readonly Button _copyAllButton;
    private bool _closed;

    public OcrResultWindow(BitmapSource image, byte[] png, string weChatDir)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(png);

        _png = png;
        _weChatDir = weChatDir;

        Title = Properties.Resources.ocr_window_title;
        Width = 760;
        Height = 460;
        MinWidth = 420;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 14;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Content = root;

        // Left: the captured region, scaled down to fit (never up).
        var imageHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(242, 242, 244)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = new Image
            {
                Source = image,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        RenderOptions.SetBitmapScalingMode(imageHost.Child, BitmapScalingMode.HighQuality);
        Grid.SetColumn(imageHost, 0);
        root.Children.Add(imageHost);

        // Right: recognized text, with a busy / error overlay on top.
        var textHost = new Grid();
        Grid.SetColumn(textHost, 2);
        root.Children.Add(textHost);

        _textBox = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(6),
            BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 224)),
            IsEnabled = false,
        };
        _textBox.SelectionChanged += (sender, e) => UpdateCopyButtons();
        _textBox.TextChanged += (sender, e) => UpdateCopyButtons();
        textHost.Children.Add(_textBox);

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
            Text = Properties.Resources.ocr_busy,
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

        // Bottom: status on the left, actions on the right.
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        Grid.SetRow(bottom, 1);
        Grid.SetColumnSpan(bottom, 3);
        root.Children.Add(bottom);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);

        _copyButton = CreateButton(Properties.Resources.ocr_copy, isDefault: true);
        _copyButton.Click += (sender, e) => CopyText(_textBox.SelectionLength > 0 ? _textBox.SelectedText : _textBox.Text);
        buttons.Children.Add(_copyButton);

        _copyAllButton = CreateButton(Properties.Resources.ocr_copy_all, isDefault: false);
        _copyAllButton.Click += (sender, e) => CopyText(_textBox.Text);
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

        UpdateCopyButtons();

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += async (sender, e) => await RecognizeAsync();
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

    private async Task RecognizeAsync()
    {
        SetBusy();
        try
        {
            var text = await WeChatOcrService.RecognizeAsync(_png, _weChatDir);
            if (_closed)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowMessage(Properties.Resources.ocr_no_text, isError: false);
                return;
            }

            Cursor = null;
            _textBox.Text = text;
            _textBox.IsEnabled = true;
            _overlay.Visibility = Visibility.Collapsed;
            UpdateCopyButtons();
            _textBox.Focus();
        }
        catch (OcrException ex)
        {
            ShowMessage(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip text extraction failed: {ex.Message}", ex, GetType());
            ShowMessage(WeChatOcrService.FailedMessage(ex.Message), isError: true);
        }
    }

    private void SetBusy()
    {
        _overlay.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Visible;
        _overlayText.Text = Properties.Resources.ocr_busy;
        _overlayText.Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108));
        _textBox.IsEnabled = false;
        Cursor = Cursors.AppStarting;
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
        UpdateCopyButtons();
    }

    private void UpdateCopyButtons()
    {
        if (_copyButton == null || _copyAllButton == null)
        {
            return;
        }

        var hasText = _textBox.IsEnabled && !string.IsNullOrEmpty(_textBox.Text);
        _copyButton.IsEnabled = hasText;
        _copyAllButton.IsEnabled = hasText;
    }

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
            Log.Exception($"Snip OCR clipboard copy failed: {ex.Message}", ex, GetType());
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
        }
    }

    private void OnClosed(object sender, EventArgs e)
    {
        // A recognition still running finishes in the background; its result is dropped.
        _closed = true;
    }
}
