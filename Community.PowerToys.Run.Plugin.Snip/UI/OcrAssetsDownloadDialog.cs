// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

using Community.PowerToys.Run.Plugin.Snip.Services;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Modal progress window for downloading OCR assets (~61 MB) from the WeChatOcr NuGet package.
/// </summary>
public sealed class OcrAssetsDownloadDialog : Window, IDisposable
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;
    private readonly Button _cancelButton;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private bool _finished;
    private bool _disposed;

    public bool Succeeded { get; private set; }

    public string ResultPath { get; private set; }

    public string ErrorMessage { get; private set; }

    public OcrAssetsDownloadDialog()
    {
        Title = Properties.Resources.ocr_download_title;
        Width = 440;
        Height = 190;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.White;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        FontSize = 13;

        var root = new StackPanel { Margin = new Thickness(16) };
        Content = root;

        root.Children.Add(new TextBlock
        {
            Text = Properties.Resources.ocr_download_body,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        _bar = new ProgressBar
        {
            Height = 16,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            IsIndeterminate = true,
        };
        root.Children.Add(_bar);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 12),
            Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 108)),
            Text = Properties.Resources.ocr_download_starting,
        };
        root.Children.Add(_status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _cancelButton = new Button
        {
            Content = Properties.Resources.ocr_download_cancel,
            MinWidth = 88,
            Height = 28,
            IsCancel = true,
        };
        _cancelButton.Click += (sender, e) =>
        {
            if (_finished)
            {
                DialogResult = Succeeded;
                return;
            }

            _cts.Cancel();
            _cancelButton.IsEnabled = false;
            _status.Text = Properties.Resources.ocr_err_download_cancelled;
        };
        buttons.Children.Add(_cancelButton);
        root.Children.Add(buttons);

        Loaded += async (sender, e) => await RunAsync();
        Closed += OnClosed;
    }

    /// <summary>
    /// Shows the dialog, downloads (or reuses cache), and returns the outcome.
    /// Must be called on an STA UI thread.
    /// </summary>
    public static (bool Ok, string Path, string Error) ShowDialogAndDownload(Window owner = null)
    {
        using var dialog = new OcrAssetsDownloadDialog();
        if (owner != null)
        {
            dialog.Owner = owner;
        }

        dialog.ShowDialog();
        return (dialog.Succeeded, dialog.ResultPath, dialog.ErrorMessage);
    }

    private async Task RunAsync()
    {
        var progress = new Progress<OcrAssetsDownloadProgress>(p =>
        {
            string source = string.IsNullOrWhiteSpace(p.SourceName) ? "…" : p.SourceName;
            if (string.Equals(p.Phase, "extract", StringComparison.Ordinal))
            {
                _bar.IsIndeterminate = true;
                _status.Text = Properties.Resources.ocr_download_extracting
                    .Replace("{0}", source, StringComparison.Ordinal);
                return;
            }

            if (p.TotalBytes is > 0)
            {
                _bar.IsIndeterminate = false;
                _bar.Value = p.Percent;
                _status.Text = Properties.Resources.ocr_download_progress
                    .Replace("{0}", FormatMb(p.DownloadedBytes), StringComparison.Ordinal)
                    .Replace("{1}", FormatMb(p.TotalBytes.Value), StringComparison.Ordinal)
                    .Replace("{2}", source, StringComparison.Ordinal);
            }
            else
            {
                _bar.IsIndeterminate = true;
                _status.Text = Properties.Resources.ocr_download_progress_unknown
                    .Replace("{0}", FormatMb(p.DownloadedBytes), StringComparison.Ordinal)
                    .Replace("{1}", source, StringComparison.Ordinal);
            }
        });

        try
        {
            var (ok, path, error) = await OcrAssetsDownloader.EnsureDownloadedAsync(
                progress,
                force: false,
                _cts.Token).ConfigureAwait(true);

            _finished = true;
            Succeeded = ok;
            ResultPath = path;
            ErrorMessage = error;

            if (ok)
            {
                _bar.IsIndeterminate = false;
                _bar.Value = 100;
                _status.Text = Properties.Resources.ocr_download_done;
                _cancelButton.Content = Properties.Resources.ocr_close;
                _cancelButton.IsEnabled = true;

                await Dispatcher.Yield(DispatcherPriority.Background);
                await Task.Delay(400).ConfigureAwait(true);
                if (!_disposed)
                {
                    DialogResult = true;
                }
            }
            else
            {
                _bar.IsIndeterminate = false;
                _status.Text = error ?? Properties.Resources.ocr_err_download_network;
                _status.Foreground = new SolidColorBrush(Color.FromRgb(214, 48, 49));
                _cancelButton.Content = Properties.Resources.ocr_close;
                _cancelButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            _finished = true;
            Succeeded = false;
            ErrorMessage = $"{Properties.Resources.ocr_err_download_network} {ex.Message}".Trim();
            _status.Text = ErrorMessage;
            _status.Foreground = new SolidColorBrush(Color.FromRgb(214, 48, 49));
            _cancelButton.Content = Properties.Resources.ocr_close;
            _cancelButton.IsEnabled = true;
        }
    }

    private void OnClosed(object sender, EventArgs e)
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Closed -= OnClosed;
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        _cts.Dispose();
    }

    private static string FormatMb(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return mb.ToString("0.0", CultureInfo.CurrentCulture);
    }
}
