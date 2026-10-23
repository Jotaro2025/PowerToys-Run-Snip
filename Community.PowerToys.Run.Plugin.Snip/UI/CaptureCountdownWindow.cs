// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Topmost, non-interactive overlay that counts down <see cref="SnipSettings.CaptureDelayMs"/>
/// before the screen is captured.
/// </summary>
public sealed class CaptureCountdownWindow : Window
{
    private readonly TextBlock _label;

    public CaptureCountdownWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Width = 280;
        Height = 72;

        _label = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(245, 245, 250)),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 18, 18, 22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 14, 20, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 16,
                ShadowDepth = 4,
                Opacity = 0.5,
            },
            IsHitTestVisible = false,
            Child = _label,
        };
        Loaded += (sender, e) => CenterOnPrimaryWorkArea();
    }

    /// <summary>
    /// Shows a countdown for <paramref name="delayMs"/> on the current dispatcher, then closes.
    /// No-op when delay is ≤ 0.
    /// </summary>
    public static async Task RunAsync(int delayMs)
    {
        if (delayMs <= 0)
        {
            return;
        }

        var window = new CaptureCountdownWindow();
        window.Show();
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < delayMs)
            {
                int left = Math.Max(0, delayMs - (int)sw.ElapsedMilliseconds);
                window.SetRemaining(left);
                int slice = Math.Min(50, Math.Max(1, left));
                await Task.Delay(slice).ConfigureAwait(true);
            }
        }
        finally
        {
            try
            {
                window.Close();
            }
            catch (Exception)
            {
                // Window may already be closed if the STA is shutting down.
            }
        }
    }

    private void SetRemaining(int remainingMs)
    {
        double seconds = remainingMs / 1000.0;
        string display = seconds >= 1.0
            ? seconds.ToString("0.0", CultureInfo.CurrentCulture) + " s"
            : remainingMs.ToString(CultureInfo.CurrentCulture) + " ms";
        _label.Text = Properties.Resources.capture_countdown_text.Replace("{0}", display, StringComparison.Ordinal);
    }

    private void CenterOnPrimaryWorkArea()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + ((work.Width - Width) / 2);
        Top = work.Top + ((work.Height - Height) / 2);
    }
}
