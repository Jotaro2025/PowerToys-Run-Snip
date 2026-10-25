// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
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
/// WeChat-style "extract text" window: the captured region on the left, the
/// recognized (editable, selectable) text on the right, plus copy actions.
/// Runs the OCR when it opens and shows a busy state until the text arrives.
/// </summary>
public sealed class OcrResultWindow : Window, IDisposable
{
    private static readonly Color Accent = Color.FromRgb(72, 210, 110);
    private static readonly Color ErrorColor = Color.FromRgb(214, 48, 49);

    private readonly BitmapSource _image;
    private readonly byte[] _png;
    private readonly Border _qrBanner;
    private readonly TextBlock _qrPayloadText;
    private readonly Button _qrCopyButton;
    private readonly Button _qrOpenUrlButton;
    private readonly TextBox _textBox;
    private readonly Border _overlay;
    private readonly ProgressBar _progress;
    private readonly TextBlock _overlayText;
    private readonly TextBlock _status;
    private readonly Button _copyButton;
    private readonly Button _exportButton;
    private readonly Button _copyAllButton;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly SnipSettings _settings;
    private readonly int _sourceHeight;
    private Uri _detectedHttpUri;
    private string _detectedCodeText;
    private bool _closed;
    private System.Collections.Generic.IReadOnlyList<OcrTextLine> _lines;
    private bool _disposed;

    public OcrResultWindow(BitmapSource image, byte[] png, SnipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(png);

        // Store an independent frozen copy: Freezable.Freeze() cascades through
        // CroppedBitmap chains, so freezing the passed bitmap directly would also
        // freeze the editor's live WriteableBitmap source (whose next Lock() in the
        // pixel magnifier throws InvalidOperationException).
        _image = image.IsFrozen ? image : BitmapPreview.MaterializeFrozen(image);
        _png = png;
        _settings = settings ?? new SnipSettings();
        _sourceHeight = _image.PixelHeight;

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
                Source = _image,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        RenderOptions.SetBitmapScalingMode(imageHost.Child, BitmapScalingMode.HighQuality);
        Grid.SetColumn(imageHost, 0);
        root.Children.Add(imageHost);

        // Right: recognized QR/barcode banner (when detected) + recognized text, with a busy / error overlay on top.
        var textHost = new Grid();
        textHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        textHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(textHost, 2);
        root.Children.Add(textHost);

        _qrPayloadText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(28, 110, 62)),
        };
        _qrCopyButton = CreateButton(Properties.Resources.qr_copy, isDefault: false);
        _qrCopyButton.MinWidth = 72;
        _qrCopyButton.Height = 26;
        _qrCopyButton.Click += (sender, e) => CopyText(_detectedCodeText);

        _qrOpenUrlButton = CreateButton(Properties.Resources.qr_open_url, isDefault: false);
        _qrOpenUrlButton.MinWidth = 72;
        _qrOpenUrlButton.Height = 26;
        _qrOpenUrlButton.Visibility = Visibility.Collapsed;
        _qrOpenUrlButton.Click += (sender, e) => OpenDetectedUrl();

        var qrDock = new DockPanel { LastChildFill = true };
        var qrButtons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(qrButtons, Dock.Right);
        qrButtons.Children.Add(_qrCopyButton);
        qrButtons.Children.Add(_qrOpenUrlButton);
        qrDock.Children.Add(qrButtons);
        qrDock.Children.Add(_qrPayloadText);

        _qrBanner = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(232, 248, 238)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(160, 224, 182)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
            Child = qrDock,
        };
        Grid.SetRow(_qrBanner, 0);
        textHost.Children.Add(_qrBanner);

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
        Grid.SetRow(_textBox, 1);
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
        Grid.SetRow(_overlay, 1);
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

        _exportButton = CreateButton(Properties.Resources.ocr_export, isDefault: false);
        _exportButton.Click += (sender, e) => ExportLines();
        buttons.Children.Add(_exportButton);

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
        SetBusy(OcrService.IsBusy(_settings)
            ? Properties.Resources.ocr_busy_waiting
            : Properties.Resources.ocr_busy);

        // _image is an independent frozen copy from the constructor, so the
        // full-frame copy below is safe off the STA.
        var image = _image;
        var scannedCodes = await Task.Run(() => QrAndBarcodeScanner.Scan(image)).ConfigureAwait(true);
        ApplyDetectedCodes(scannedCodes);

        try
        {
            var lines = await OcrService.RecognizeLinesAsync(_png, _settings, _cts.Token);
            var text = string.Join(Environment.NewLine, lines.Select(l => l.Text));
            if (_closed)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrEmpty(_detectedCodeText))
            {
                text = _detectedCodeText;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowMessage(Properties.Resources.ocr_no_text, isError: false);
                return;
            }

            _lines = lines;
            Cursor = null;
            _textBox.Text = text;
            _textBox.IsEnabled = true;
            _overlay.Visibility = Visibility.Collapsed;
            UpdateCopyButtons();
            ApplyTallImageStatusHint();
            _textBox.Focus();
        }
        catch (OperationCanceledException)
        {
            // Window closed while OCR was running / queued.
        }
        catch (ObjectDisposedException)
        {
            // CTS or OCR resources disposed after close.
        }
        catch (OcrException ex)
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

            Log.Exception($"Snip text extraction failed: {ex.Message}", ex, GetType());
            ShowMessage(WeChatOcrService.FailedMessage(ex.Message), isError: true);
        }
    }

    private void ApplyTallImageStatusHint()
    {
        string tip = WindowsOcrService.ConsumeTallImageHint();
        if (tip == null
            && OcrService.ResolveEngine(_settings) == Models.OcrEngineKind.WeChat)
        {
            tip = WindowsOcrService.TryBuildTallPreferWindowsHint(_sourceHeight);
        }

        if (string.IsNullOrEmpty(tip) || _status == null)
        {
            return;
        }

        _status.Foreground = new SolidColorBrush(Color.FromRgb(180, 120, 40));
        _status.Text = tip;
    }

    private void SetBusy(string message)
    {
        _overlay.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Visible;
        _overlayText.Text = message;
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

    private void ApplyDetectedCodes(System.Collections.Generic.IReadOnlyList<QrAndBarcodeScanner.ScanResult> codes)
    {
        if (codes == null || codes.Count == 0)
        {
            _qrBanner.Visibility = Visibility.Collapsed;
            return;
        }

        var first = codes[0];
        _detectedCodeText = first.Text;
        _detectedHttpUri = first.HttpUri;
        _qrPayloadText.Text = $"{Properties.Resources.qr_detected_prefix}[{first.Format}] {first.Text}";
        _qrPayloadText.ToolTip = first.Text;
        _qrOpenUrlButton.Visibility = _detectedHttpUri != null ? Visibility.Visible : Visibility.Collapsed;
        _qrBanner.Visibility = Visibility.Visible;
    }

    private void OpenDetectedUrl()
    {
        if (_detectedHttpUri == null)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_detectedHttpUri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip QR open URL failed: {ex.Message}", ex, GetType());
        }
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

    private void ExportLines()
    {
        if (_lines == null || _lines.Count == 0)
        {
            return;
        }

        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Properties.Resources.ocr_export,
                DefaultExt = ".md",
                Filter = Properties.Resources.ocr_export_filter,
                AddExtension = true,
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var format = dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? OcrExport.Format.Json
                : OcrExport.Format.Markdown;
            System.IO.File.WriteAllText(dialog.FileName, OcrExport.Render(_lines, format));
            _status.Foreground = new SolidColorBrush(Color.FromRgb(48, 175, 85));
            _status.Text = $"{Properties.Resources.ocr_export_saved}: {System.IO.Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip OCR export failed: {ex.Message}", ex, GetType());
            _status.Foreground = new SolidColorBrush(ErrorColor);
            _status.Text = ex.Message;
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
