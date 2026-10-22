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
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Image-first translate window: shows the original screenshot with a lightweight
/// processing animation, then swaps to the finished translated bitmap. OCR line
/// editors stay collapsed under an optional “adjust translations” expander.
/// </summary>
public sealed class TranslateResultWindow : Window, IDisposable
{
    private static readonly Color Accent = Color.FromRgb(72, 210, 110);
    private static readonly Color ErrorColor = Color.FromRgb(214, 48, 49);

    private readonly byte[] _png;
    private readonly BitmapSource _originalImage;
    private readonly SnipSettings _settings;
    private readonly Image _imageView;
    private readonly Border _imageHost;
    private readonly Grid _imageStage;
    private readonly Border _dimLayer;
    private readonly Border _overlay;
    private readonly Border _busyBadge;
    private readonly ProgressBar _progress;
    private readonly TextBlock _overlayText;
    private readonly Rectangle _scanLine;
    private readonly Rectangle _shimmer;
    private readonly TranslateTransform _scanTransform;
    private readonly TranslateTransform _shimmerTransform;
    private readonly Expander _editExpander;
    private readonly StackPanel _linesPanel;
    private readonly ScrollViewer _linesScroll;
    private readonly TextBlock _status;
    private readonly Button _copyAllButton;
    private readonly Button _copyImageButton;
    private readonly Button _reapplyButton;
    private readonly Button _saveImageButton;
    private readonly List<TextBox> _translationBoxes = new();
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private Storyboard _busyStoryboard;
    private IReadOnlyList<string> _translations = [];
    private bool _busyAnimating;
    private bool _closed;
    private bool _disposed;
    private bool _actionBusy;
    private IReadOnlyList<OcrTextLine> _lines = [];

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
        Width = 760;
        Height = 560;
        MinWidth = 420;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 14;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        _imageView = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.72,
        };
        RenderOptions.SetBitmapScalingMode(_imageView, BitmapScalingMode.HighQuality);

        _imageStage = new Grid { ClipToBounds = true };
        _imageStage.Children.Add(_imageView);

        // Soft dim while processing (kept under the scan/shimmer layers).
        _dimLayer = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(70, 20, 24, 32)),
            IsHitTestVisible = false,
        };
        _imageStage.Children.Add(_dimLayer);

        _scanTransform = new TranslateTransform();
        _scanLine = new Rectangle
        {
            Height = 2,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Fill = new LinearGradientBrush(
                Color.FromArgb(0, 72, 210, 110),
                Color.FromArgb(220, 72, 210, 110),
                new Point(0, 0.5),
                new Point(1, 0.5)),
            Opacity = 0.9,
            IsHitTestVisible = false,
            RenderTransform = _scanTransform,
            Visibility = Visibility.Collapsed,
        };
        _imageStage.Children.Add(_scanLine);

        _shimmerTransform = new TranslateTransform { X = -120 };
        _shimmer = new Rectangle
        {
            Width = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false,
            Opacity = 0.35,
            Visibility = Visibility.Collapsed,
            RenderTransform = _shimmerTransform,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(90, 255, 255, 255), 0.5),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                },
            },
        };
        _imageStage.Children.Add(_shimmer);

        _progress = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 3,
            Width = 140,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Accent),
        };
        _overlayText = new TextBlock
        {
            Text = Properties.Resources.tr_busy,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = Brushes.White,
            FontSize = 13,
            MaxWidth = 280,
        };
        var badgeContent = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        badgeContent.Children.Add(_progress);
        badgeContent.Children.Add(_overlayText);
        _busyBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(210, 28, 32, 40)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 14, 18, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = badgeContent,
        };

        // Full-stage overlay hosts the status badge (and error messages).
        _overlay = new Border
        {
            Background = Brushes.Transparent,
            Child = _busyBadge,
        };
        _imageStage.Children.Add(_overlay);

        _imageHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(242, 242, 244)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8),
            Child = _imageStage,
        };
        Grid.SetRow(_imageHost, 0);
        root.Children.Add(_imageHost);

        _linesPanel = new StackPanel();
        _linesScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _linesPanel,
            MaxHeight = 180,
        };
        _editExpander = new Expander
        {
            Header = Properties.Resources.tr_edit_translations,
            IsExpanded = false,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0),
            Content = _linesScroll,
        };
        Grid.SetRow(_editExpander, 1);
        root.Children.Add(_editExpander);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        Grid.SetRow(bottom, 2);
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

        _copyImageButton = CreateButton(Properties.Resources.tr_copy_image, isDefault: true);
        _copyImageButton.Click += (sender, e) => CopyImage();
        buttons.Children.Add(_copyImageButton);

        _copyAllButton = CreateButton(Properties.Resources.tr_copy_all, isDefault: false);
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
        Loaded += async (sender, e) =>
        {
            StartBusyAnimation();
            await RunPipelineAsync();
        };
        Closed += OnClosed;
        SizeChanged += OnWindowSizeChanged;
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_busyAnimating)
        {
            StartBusyAnimation();
        }
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

            _translations = translations?.ToList() ?? [];

            // Keep editors built but collapsed; shown only when the user expands Edit translations.
            BuildTranslationEditors(_lines, _translations);
            await ShowFinishedImageAsync(rendered);
            UpdateActionButtons(enabled: true);
            _status.Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108));
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

    private async Task ShowFinishedImageAsync(BitmapSource rendered)
    {
        StopBusyAnimation();
        Cursor = null;
        _overlay.Visibility = Visibility.Collapsed;
        _editExpander.Visibility = Visibility.Visible;
        _editExpander.IsExpanded = false;

        // Brief crossfade: fade out original → swap → fade in result.
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fadeOut.Completed += (sender, e) => tcs.TrySetResult();
        _imageView.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        await tcs.Task.ConfigureAwait(true);
        if (_closed)
        {
            return;
        }

        _imageView.Source = rendered;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        _imageView.BeginAnimation(UIElement.OpacityProperty, fadeIn);
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

            // Source OCR stays subtle and secondary - not the primary UI.
            stack.Children.Add(new TextBlock
            {
                Text = lines[i].Text,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 156)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });

            var box = new TextBox
            {
                Text = i < translations.Count ? (translations[i] ?? string.Empty) : string.Empty,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MinHeight = 28,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
            };
            _translationBoxes.Add(box);
            stack.Children.Add(box);

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
        _busyBadge.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Visible;
        _overlayText.Text = message;
        _overlayText.Foreground = Brushes.White;
        _editExpander.Visibility = Visibility.Collapsed;
        _editExpander.IsExpanded = false;
        Cursor = Cursors.AppStarting;
        UpdateActionButtons(enabled: false);
        StartBusyAnimation();
    }

    private void ShowMessage(string message, bool isError)
    {
        if (_closed)
        {
            return;
        }

        StopBusyAnimation();
        Cursor = null;
        _dimLayer.Visibility = Visibility.Visible;
        _progress.Visibility = Visibility.Collapsed;
        _overlay.Visibility = Visibility.Visible;
        _busyBadge.Visibility = Visibility.Visible;
        _overlayText.Text = message;
        _overlayText.Foreground = isError ? new SolidColorBrush(ErrorColor) : Brushes.White;
        _editExpander.Visibility = Visibility.Collapsed;
        _imageView.BeginAnimation(UIElement.OpacityProperty, null);
        _imageView.Opacity = 0.55;
        UpdateActionButtons(enabled: false);
    }

    private void StartBusyAnimation()
    {
        if (_closed)
        {
            return;
        }

        _busyAnimating = true;
        _dimLayer.Visibility = Visibility.Visible;
        _scanLine.Visibility = Visibility.Visible;
        _shimmer.Visibility = Visibility.Visible;

        if (_busyStoryboard != null)
        {
            _busyStoryboard.Stop();
            _busyStoryboard.Children.Clear();
        }

        _busyStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        double stageHeight = _imageStage.ActualHeight > 1 ? _imageStage.ActualHeight : Height;
        double stageWidth = _imageStage.ActualWidth > 1 ? _imageStage.ActualWidth : Width;

        var scanAnim = new DoubleAnimation
        {
            From = -8,
            To = Math.Max(stageHeight, 200) + 8,
            Duration = TimeSpan.FromSeconds(1.8),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(scanAnim, _scanLine);
        Storyboard.SetTargetProperty(scanAnim, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
        _busyStoryboard.Children.Add(scanAnim);

        var shimmerAnim = new DoubleAnimation
        {
            From = -120,
            To = Math.Max(stageWidth, 320) + 40,
            Duration = TimeSpan.FromSeconds(2.4),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(shimmerAnim, _shimmer);
        Storyboard.SetTargetProperty(shimmerAnim, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)"));
        _busyStoryboard.Children.Add(shimmerAnim);

        var pulse = new DoubleAnimation
        {
            From = 0.88,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(900),
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(pulse, _busyBadge);
        Storyboard.SetTargetProperty(pulse, new PropertyPath(UIElement.OpacityProperty));
        _busyStoryboard.Children.Add(pulse);

        _busyStoryboard.Begin();
    }

    private void StopBusyAnimation()
    {
        _busyAnimating = false;
        if (_busyStoryboard != null)
        {
            _busyStoryboard.Stop();
            _busyStoryboard.Children.Clear();
            _busyStoryboard = null;
        }

        _scanLine.Visibility = Visibility.Collapsed;
        _shimmer.Visibility = Visibility.Collapsed;
        _dimLayer.Visibility = Visibility.Collapsed;
        _scanTransform.Y = 0;
        _shimmerTransform.X = -120;
        _busyBadge.BeginAnimation(UIElement.OpacityProperty, null);
        _busyBadge.Opacity = 1;
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
        if (_closed || _actionBusy || _lines.Count == 0)
        {
            return;
        }

        var translations = CollectTranslations();
        if (translations.Count != _lines.Count)
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
            var rendered = await Task.Run(() => ImageTranslateRenderer.Render(_originalImage, _lines, translations));
            if (_closed)
            {
                return;
            }

            _translations = translations;
            _imageView.BeginAnimation(UIElement.OpacityProperty, null);
            _imageView.Opacity = 1;
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
    {
        if (_translationBoxes.Count == _lines.Count && _translationBoxes.Count > 0)
        {
            return _translationBoxes.Select(b => b.Text ?? string.Empty).ToList();
        }

        return _translations.ToList();
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
        StopBusyAnimation();
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
