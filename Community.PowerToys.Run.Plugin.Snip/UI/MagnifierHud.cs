// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Magnifier HUD shown while the user moves the cursor (or drags a handle):
/// a zoomed pixel grid, the sampled color swatch, coordinates, and HEX/RGB
/// text. Owns its whole visual tree and the zoomed-grid bitmap; the host
/// feeds it the frozen source bitmap and monitor bounds each update.
/// </summary>
internal sealed class MagnifierHud
{
    private readonly Border _hud;
    private readonly System.Windows.Controls.Image _image;
    private readonly Border _swatch;
    private readonly TextBlock _coordText;
    private readonly TextBlock _colorText;
    private readonly TextBlock _hintText;
    private readonly WriteableBitmap _gridBitmap;
    private SolidColorBrush _swatchBrush;

    private byte _sampledR;
    private byte _sampledG;
    private byte _sampledB;

    /// <summary>True after at least one pixel was sampled (C/Shift+C precondition).</summary>
    public bool HasSampledPixel { get; private set; }

    public MagnifierHud(Canvas host)
    {
        ArgumentNullException.ThrowIfNull(host);

        _gridBitmap = new WriteableBitmap(
            PixelMagnifier.OutputPixelSize,
            PixelMagnifier.OutputPixelSize,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        _image = new System.Windows.Controls.Image
        {
            Width = PixelMagnifier.OutputPixelSize,
            Height = PixelMagnifier.OutputPixelSize,
            Source = _gridBitmap,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);

        _swatch = new Border
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _coordText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 2),
        };
        _colorText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(220, 224, 232)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var colorRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 1, 0, 2),
        };
        colorRow.Children.Add(_swatch);
        colorRow.Children.Add(_colorText);

        _hintText = new TextBlock
        {
            Text = "[C] HEX  [Shift+C] RGB",
            Foreground = new SolidColorBrush(Color.FromRgb(150, 156, 168)),
            FontSize = 10,
        };
        var hudStack = new StackPanel();
        hudStack.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = _image,
        });
        hudStack.Children.Add(_coordText);
        hudStack.Children.Add(colorRow);
        hudStack.Children.Add(_hintText);

        _hud = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 22, 24, 28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = hudStack,
        };
        host.Children.Add(_hud);
        Panel.SetZIndex(_hud, 35);
    }

    public void Hide() => _hud.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Samples the pixel under <paramref name="dipPoint"/> from the (unfrozen)
    /// <paramref name="source"/> and refreshes the HUD. When <paramref name="showGrid"/>
    /// is false (a selection exists and the user is not dragging), only the sample
    /// state updates and the HUD stays hidden.
    /// </summary>
    public unsafe void UpdateAt(
        WriteableBitmap source,
        double dpiScale,
        Point dipPoint,
        bool showGrid,
        Rect dragSelection,
        bool dragging,
        Rect monitorArea)
    {
        if (source == null || _gridBitmap == null || source.IsFrozen || _gridBitmap.IsFrozen)
        {
            _hud.Visibility = Visibility.Collapsed;
            return;
        }

        int sw = source.PixelWidth;
        int sh = source.PixelHeight;
        if (sw <= 0 || sh <= 0)
        {
            return;
        }

        int px = Math.Clamp((int)Math.Round(dipPoint.X * dpiScale), 0, sw - 1);
        int py = Math.Clamp((int)Math.Round(dipPoint.Y * dpiScale), 0, sh - 1);

        source.Lock();
        _gridBitmap.Lock();
        try
        {
            var srcPtr = (byte*)source.BackBuffer;
            int srcStride = source.BackBufferStride;
            PixelMagnifier.SamplePixelBgra(srcPtr, srcStride, sw, sh, px, py, out _sampledR, out _sampledG, out _sampledB);
            HasSampledPixel = true;

            if (showGrid)
            {
                PixelMagnifier.FillZoomedGrid(
                    srcPtr,
                    srcStride,
                    sw,
                    sh,
                    px,
                    py,
                    (byte*)_gridBitmap.BackBuffer,
                    _gridBitmap.BackBufferStride);
                _gridBitmap.AddDirtyRect(new Int32Rect(0, 0, PixelMagnifier.OutputPixelSize, PixelMagnifier.OutputPixelSize));
            }
        }
        finally
        {
            _gridBitmap.Unlock();
            source.Unlock();
        }

        if (!showGrid)
        {
            _hud.Visibility = Visibility.Collapsed;
            return;
        }

        // Swatch brush is frozen and reused; only the color changes per move.
        if (_swatchBrush == null)
        {
            _swatchBrush = new SolidColorBrush(Color.FromRgb(_sampledR, _sampledG, _sampledB));
            _swatch.Background = _swatchBrush;
        }
        else
        {
            _swatchBrush.Color = Color.FromRgb(_sampledR, _sampledG, _sampledB);
        }

        if (dragging)
        {
            int selW = Math.Max(1, (int)Math.Round(dragSelection.Width * dpiScale));
            int selH = Math.Max(1, (int)Math.Round(dragSelection.Height * dpiScale));
            _coordText.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{selW} × {selH}  ({px}, {py})");
        }
        else
        {
            _coordText.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({px}, {py})");
        }

        _colorText.Text = $"{PixelMagnifier.FormatHex(_sampledR, _sampledG, _sampledB)}  {PixelMagnifier.FormatRgb(_sampledR, _sampledG, _sampledB)}";

        _hud.Visibility = Visibility.Visible;
        _hud.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var hudPos = PixelMagnifier.ComputeHudPosition(dipPoint, _hud.DesiredSize, monitorArea);
        Canvas.SetLeft(_hud, hudPos.X);
        Canvas.SetTop(_hud, hudPos.Y);
    }

    /// <summary>Copies the last sampled color as HEX (default) or RGB and flashes the hint.</summary>
    public void CopySampledColorToClipboard(bool copyRgb)
    {
        string value = copyRgb
            ? PixelMagnifier.FormatRgb(_sampledR, _sampledG, _sampledB)
            : PixelMagnifier.FormatHex(_sampledR, _sampledG, _sampledB);
        try
        {
            Clipboard.SetText(value);
            _hintText.Foreground = new SolidColorBrush(Color.FromRgb(72, 210, 110));
            _hintText.Text = $"{Properties.Resources.ocr_copied}: {value}";
        }
        catch (Exception ex)
        {
            Log.Exception($"MagnifierHud CopySampledColorToClipboard failed: {ex.Message}", ex, GetType());
        }
    }
}
