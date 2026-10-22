// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

using Community.PowerToys.Run.Plugin.Snip.Models;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Floating annotation toolbar with grouped tools, color/width pickers, and actions.
/// </summary>
public sealed class SnipToolbar : Border
{
    private static readonly Color Accent = Color.FromRgb(72, 210, 110);

    private static readonly Color[] Palette =
    {
        Color.FromRgb(238, 32, 77),
        Color.FromRgb(0, 122, 255),
        Color.FromRgb(52, 199, 89),
        Color.FromRgb(255, 149, 0),
        Color.FromRgb(255, 214, 10),
        Color.FromRgb(255, 255, 255),
        Color.FromRgb(0, 0, 0),
    };

    private readonly Dictionary<string, ToolbarButton> _toolButtons = new();
    private readonly ToolbarButton _undoButton;
    private readonly ToolbarButton _redoButton;
    private readonly Dictionary<Color, Border> _colorChips = new();
    private readonly Dictionary<double, Border> _widthChips = new();
    private readonly List<double> _widthPresets = new() { 2, 3, 5, 8 };

    public SnipToolbar(EditRequest request)
    {
        ShowSaveButton = request.ShowSaveButton;
        CurrentStroke = request.DefaultStroke ?? Color.FromRgb(238, 32, 77);
        CurrentStrokeWidth = request.DefaultStrokeWidth;
        ActiveTool = request.InitialTool;

        Background = new SolidColorBrush(Color.FromArgb(248, 26, 26, 29));
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(8, 6, 8, 6);
        BorderBrush = new SolidColorBrush(Color.FromArgb(40, 200, 200, 210));
        BorderThickness = new Thickness(0.5);

        var root = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Child = root;

        var toolGroup = CreateGroup(out var tools);
        AddTool(tools, ToolIds.Select, ToolIcons.Select, Properties.Resources.tip_select);
        AddTool(tools, ToolIds.Rect, ToolIcons.Rect, Properties.Resources.tip_rect);
        AddTool(tools, ToolIds.Ellipse, ToolIcons.Ellipse, Properties.Resources.tip_ellipse);
        AddTool(tools, ToolIds.Arrow, ToolIcons.Arrow, Properties.Resources.tip_arrow);
        AddTool(tools, ToolIds.Pen, ToolIcons.Pen, Properties.Resources.tip_pen);
        AddTool(tools, ToolIds.Text, ToolIcons.Text, Properties.Resources.tip_text);
        AddTool(tools, ToolIds.Mosaic, ToolIcons.Mosaic, Properties.Resources.tip_mosaic);
        root.Children.Add(toolGroup);

        root.Children.Add(Separator());

        var colorGroup = CreateGroup(out var colors);
        AddColorChips(colors);
        root.Children.Add(colorGroup);

        root.Children.Add(Separator());

        var widthGroup = CreateGroup(out var widths);
        AddWidthChips(widths);
        root.Children.Add(widthGroup);

        root.Children.Add(Separator());

        var actionGroup = CreateGroup(out var actions);
        _undoButton = AddIcon(actions, ToolIcons.Undo, Properties.Resources.tip_undo);
        _undoButton.IsEnabled = false;
        _undoButton.Opacity = 0.38;
        _undoButton.Click += (sender, e) => UndoClicked?.Invoke(this, EventArgs.Empty);

        _redoButton = AddIcon(actions, ToolIcons.Redo, Properties.Resources.tip_redo);
        _redoButton.IsEnabled = false;
        _redoButton.Opacity = 0.38;
        _redoButton.Click += (sender, e) => RedoClicked?.Invoke(this, EventArgs.Empty);

        AddIcon(actions, ToolIcons.Ocr, Properties.Resources.tip_ocr).Click += (sender, e) => ExtractTextClicked?.Invoke(this, EventArgs.Empty);
        AddIcon(actions, ToolIcons.Translate, Properties.Resources.tip_translate).Click += (sender, e) => TranslateImageClicked?.Invoke(this, EventArgs.Empty);
        AddIcon(actions, ToolIcons.Pin, Properties.Resources.tip_pin).Click += (sender, e) => PinClicked?.Invoke(this, EventArgs.Empty);

        if (ShowSaveButton)
        {
            AddIcon(actions, ToolIcons.Save, Properties.Resources.tip_save).Click += (sender, e) => SaveClicked?.Invoke(this, EventArgs.Empty);
        }

        AddIcon(actions, ToolIcons.Cancel, Properties.Resources.tip_cancel).Click += (sender, e) => CancelClicked?.Invoke(this, EventArgs.Empty);

        var done = AddIcon(actions, ToolIcons.Done, Properties.Resources.tip_done);
        done.IsAccent = true;
        done.Click += (sender, e) => ConfirmClicked?.Invoke(this, EventArgs.Empty);
        root.Children.Add(actionGroup);

        UpdateActiveTool();
        UpdateColorChips();
        UpdateWidthChips();
    }

    public event EventHandler<string> ToolChanged;

    public event EventHandler UndoClicked;

    public event EventHandler RedoClicked;

    public event EventHandler SaveClicked;

    public event EventHandler ExtractTextClicked;

    public event EventHandler TranslateImageClicked;

    public event EventHandler PinClicked;

    public event EventHandler CancelClicked;

    public event EventHandler ConfirmClicked;

    public event EventHandler<Color> ColorChanged;

    public event EventHandler<double> StrokeWidthChanged;

    public string ActiveTool { get; private set; }

    public bool ShowSaveButton { get; }

    public Color CurrentStroke { get; private set; }

    public double CurrentStrokeWidth { get; private set; }

    public void NotifyHistory(int undoCount, int redoCount)
    {
        _undoButton.IsEnabled = undoCount > 0;
        _undoButton.Opacity = undoCount > 0 ? 1.0 : 0.38;
        _redoButton.IsEnabled = redoCount > 0;
        _redoButton.Opacity = redoCount > 0 ? 1.0 : 0.38;
    }

    public void SetActiveTool(string id)
    {
        if (ActiveTool == id)
        {
            return;
        }

        ActiveTool = id;
        UpdateActiveTool();
        ToolChanged?.Invoke(this, id);
    }

    private static Border CreateGroup(out StackPanel inner)
    {
        inner = new StackPanel { Orientation = Orientation.Horizontal };
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 20, 20, 22)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3, 2, 3, 2),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 200, 200, 210)),
            BorderThickness = new Thickness(0.5),
            Child = inner,
        };
    }

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 22,
        Margin = new Thickness(2, 0, 2, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromArgb(40, 200, 200, 210)),
    };

    private void AddTool(Panel panel, string id, string geom, string tip)
    {
        var b = new ToolbarButton();
        b.Geometry = geom;
        b.ToolTip = tip;
        b.Margin = new Thickness(1, 0, 1, 0);
        b.Click += (sender, e) => SetActiveTool(id);
        _toolButtons[id] = b;
        panel.Children.Add(b);
    }

    private ToolbarButton AddIcon(Panel panel, string geom, string tip)
    {
        var b = new ToolbarButton();
        b.Geometry = geom;
        b.ToolTip = tip;
        b.Margin = new Thickness(1, 0, 1, 0);
        panel.Children.Add(b);
        return b;
    }

    private void AddColorChips(StackPanel row)
    {
        foreach (var c in Palette)
        {
            var chip = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(c),
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(1.5),
                Cursor = Cursors.Hand,
                Margin = new Thickness(1, 0, 1, 0),
                Tag = c,
            };
            chip.MouseEnter += (sender, e) => chip.BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255));
            chip.MouseLeave += (sender, e) => UpdateColorChipBorder(chip, c);
            chip.MouseLeftButtonDown += (sender, e) =>
            {
                CurrentStroke = c;
                UpdateColorChips();
                ColorChanged?.Invoke(this, c);
                e.Handled = true;
            };
            _colorChips[c] = chip;
            row.Children.Add(chip);
        }
    }

    private void AddWidthChips(StackPanel row)
    {
        for (int i = 0; i < _widthPresets.Count; i++)
        {
            var w = _widthPresets[i];
            var size = 5 + (i * 2);
            var chip = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Margin = new Thickness(1, 0, 1, 0),
                Tag = w,
                Child = new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                },
            };
            chip.MouseEnter += (sender, e) =>
            {
                if ((double)chip.Tag != CurrentStrokeWidth)
                {
                    chip.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
                }
            };
            chip.MouseLeave += (sender, e) => UpdateWidthChip(chip, w);
            chip.MouseLeftButtonDown += (sender, e) =>
            {
                CurrentStrokeWidth = w;
                UpdateWidthChips();
                StrokeWidthChanged?.Invoke(this, w);
                e.Handled = true;
            };
            _widthChips[w] = chip;
            row.Children.Add(chip);
        }
    }

    private void UpdateActiveTool()
    {
        foreach (var (k, b) in _toolButtons)
        {
            b.IsActive = k == ActiveTool;
        }
    }

    private void UpdateColorChips()
    {
        foreach (var (c, chip) in _colorChips)
        {
            UpdateColorChipBorder(chip, c);
        }
    }

    private void UpdateColorChipBorder(Border chip, Color c)
    {
        var active = c == CurrentStroke;
        chip.BorderBrush = new SolidColorBrush(active ? Accent : Color.FromArgb(80, 255, 255, 255));
        chip.BorderThickness = new Thickness(active ? 2.5 : 1.5);
    }

    private void UpdateWidthChips()
    {
        foreach (var (w, chip) in _widthChips)
        {
            UpdateWidthChip(chip, w);
        }
    }

    private void UpdateWidthChip(Border chip, double w)
    {
        var active = Math.Abs(w - CurrentStrokeWidth) < 0.01;
        chip.Background = active
            ? new SolidColorBrush(Color.FromArgb(52, 72, 210, 110))
            : Brushes.Transparent;
        chip.BorderBrush = new SolidColorBrush(active
            ? Color.FromArgb(160, 72, 210, 110)
            : Color.FromArgb(48, 255, 255, 255));
    }
}
