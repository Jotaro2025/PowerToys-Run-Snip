// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Core.Annotations;
using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Drawing surface with creation, selection, move, resize, and delete support.
/// Annotations live in window (logical) coordinates; the mosaic samples the
/// captured bitmap through <see cref="SourceBitmap"/>.
/// </summary>
public sealed class AnnotationCanvas : FrameworkElement
{
    private static readonly Color SelectionColor = Color.FromRgb(72, 210, 110);
    private static readonly Typeface TextTypeface = new Typeface("Segoe UI");
    private readonly List<Point> _penPoints = new();
    private readonly Stack<Annotation> _redoStack = new();

    private Point? _start;
    private bool _dragging;
    private Annotation _livePreview;

    private AnnotationEditor.HandleKind? _editHandle;
    private Point _editStartPoint;
    private Annotation _editSnapshot;
    private Rect _editStartBounds;
    private DateTime _lastClickUtc;
    private int _lastClickIndex = -1;

    private TextBox _inlineTextBox;
    private int _inlineTextIndex = -1;

    /// <summary>Reusable BGRA overlay for mosaic tiles (avoids per-tile SolidColorBrush).</summary>
    private WriteableBitmap _mosaicOverlay;

    /// <summary>Throttle mosaic drag invalidates (~1/frame).</summary>
    private DateTime _lastMosaicInvalidateUtc = DateTime.MinValue;

    private const int MosaicInvalidateMinMs = 16;

    public AnnotationCanvas()
    {
        Focusable = true;
    }

    public Color CurrentStroke { get; set; } = Color.FromRgb(238, 32, 77);

    public double CurrentStrokeWidth { get; set; } = 3.0;

    public string ActiveTool { get; set; } = ToolIds.Rect;

    public System.Windows.Media.Imaging.WriteableBitmap SourceBitmap { get; set; }

    /// <summary>Offset of the source bitmap inside this canvas (logical units).</summary>
    public Point SourceOffset { get; set; }

    /// <summary>Logical-to-physical scale when sampling <see cref="SourceBitmap"/> (1.0 = 100% DPI).</summary>
    public double SourceDpiScale { get; set; } = 1.0;

    public List<Annotation> Items { get; } = new();

    public int? SelectedIndex { get; private set; }

    public int UndoLimit { get; set; } = 50;

    public event EventHandler<Annotation> Committed;

    /// <summary>Raised when undo/redo availability changes. Argument is unused; query <see cref="UndoDepth"/> / <see cref="RedoDepth"/>.</summary>
    public event EventHandler HistoryChanged;

    /// <summary>Number of annotations that can still be undone (Items count).</summary>
    public int UndoDepth => Items.Count;

    /// <summary>Number of undone annotations that can be redone.</summary>
    public int RedoDepth => _redoStack.Count;

    public void Add(Annotation a)
    {
        _redoStack.Clear();
        Items.Add(a);
        if (Items.Count > UndoLimit)
        {
            Items.RemoveAt(0);
        }

        Committed?.Invoke(this, a);
        NotifyHistory();
        InvalidateVisual();
    }

    public bool Undo()
    {
        if (Items.Count == 0)
        {
            return false;
        }

        var removed = Items[Items.Count - 1];
        Items.RemoveAt(Items.Count - 1);
        _redoStack.Push(removed);
        if (SelectedIndex >= Items.Count)
        {
            SelectedIndex = null;
        }

        NotifyHistory();
        InvalidateVisual();
        return true;
    }

    public bool Redo()
    {
        if (_redoStack.Count == 0)
        {
            return false;
        }

        Items.Add(_redoStack.Pop());
        if (Items.Count > UndoLimit)
        {
            Items.RemoveAt(0);
        }

        NotifyHistory();
        InvalidateVisual();
        return true;
    }

    public void DeleteSelected()
    {
        if (SelectedIndex is not int idx || idx < 0 || idx >= Items.Count)
        {
            return;
        }

        _redoStack.Push(Items[idx]);
        Items.RemoveAt(idx);
        SelectedIndex = null;
        NotifyHistory();
        InvalidateVisual();
    }

    private void NotifyHistory() => HistoryChanged?.Invoke(this, EventArgs.Empty);

    public void ClearSelection() => Select(null);

    public void Select(int? index)
    {
        SelectedIndex = index;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        RenderCore(drawingContext, includeChrome: true, allowMosaicPreview: true);
    }

    /// <summary>
    /// Draws only the annotations (no selection chrome). Used when rendering the
    /// final image offscreen. Always uses full-quality mosaic (export look).
    /// </summary>
    internal void RenderAnnotations(DrawingContext drawingContext)
    {
        RenderCore(drawingContext, includeChrome: false, allowMosaicPreview: false);
    }

    private void RenderCore(DrawingContext drawingContext, bool includeChrome, bool allowMosaicPreview)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            DrawAnnotation(drawingContext, Items[i], allowMosaicPreview);
            if (includeChrome && SelectedIndex == i)
            {
                DrawSelectionChrome(drawingContext, Items[i]);
            }
        }

        if (_livePreview != null)
        {
            DrawAnnotation(drawingContext, _livePreview, allowMosaicPreview);
        }
    }

    private void DrawSelectionChrome(DrawingContext drawingContext, Annotation a)
    {
        var bounds = AnnotationEditor.GetBounds(a);
        if (bounds.Width > 0)
        {
            var pen = new Pen(new SolidColorBrush(SelectionColor), 1.5);
            pen.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
            drawingContext.DrawRectangle(null, pen, bounds);
        }

        foreach (var (kind, center) in AnnotationEditor.GetHandleCenters(a))
        {
            var fill = kind is AnnotationEditor.HandleKind.StartPoint or AnnotationEditor.HandleKind.EndPoint
                ? new SolidColorBrush(Color.FromRgb(255, 200, 60))
                : new SolidColorBrush(Colors.White);
            var rect = new Rect(center.X - 4, center.Y - 4, 8, 8);
            drawingContext.DrawRectangle(fill, new Pen(new SolidColorBrush(SelectionColor), 1.2), rect);
        }
    }

    private void DrawAnnotation(DrawingContext drawingContext, Annotation a, bool allowMosaicPreview)
    {
        switch (a)
        {
            case PenAnnotation pen:
                if (pen.Points.Count < 2)
                {
                    return;
                }

                var p = new Pen(new SolidColorBrush(pen.Stroke), pen.StrokeWidth)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                drawingContext.DrawGeometry(null, p, CreatePolyline(pen.Points, false));
                break;
            case ArrowAnnotation arr:
                DrawArrow(drawingContext, arr);
                break;
            case RectAnnotation r:
                drawingContext.DrawRectangle(
                    null,
                    new Pen(new SolidColorBrush(r.Stroke), r.StrokeWidth),
                    r.Rect);
                break;
            case EllipseAnnotation e:
                drawingContext.DrawEllipse(
                    null,
                    new Pen(new SolidColorBrush(e.Stroke), e.StrokeWidth),
                    new Point(e.Rect.X + (e.Rect.Width / 2), e.Rect.Y + (e.Rect.Height / 2)),
                    e.Rect.Width / 2,
                    e.Rect.Height / 2);
                break;
            case TextAnnotation t:
                {
                    var ft = new FormattedText(
                        t.Text,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        TextTypeface,
                        t.FontSize,
                        new SolidColorBrush(t.Stroke),
                        1.0);
                    drawingContext.DrawText(ft, t.Origin);
                    break;
                }

            case MosaicAnnotation m:
                if (SourceBitmap == null)
                {
                    return;
                }

                bool preview = allowMosaicPreview && ShouldPreviewMosaic(m);
                DrawMosaic(drawingContext, m, preview);
                break;
        }
    }

    private static StreamGeometry CreatePolyline(IReadOnlyList<Point> points, bool isFilled)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled, false);
            for (int i = 1; i < points.Count; i++)
            {
                ctx.LineTo(points[i], false, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void DrawArrow(DrawingContext dc, ArrowAnnotation a)
    {
        var pen = new Pen(new SolidColorBrush(a.Stroke), a.StrokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        dc.DrawGeometry(null, pen, CreatePolyline(new[] { a.Start, a.End }, false));

        var dx = a.End.X - a.Start.X;
        var dy = a.End.Y - a.Start.Y;
        var len = Math.Sqrt((dx * dx) + (dy * dy));
        if (len < 1)
        {
            return;
        }

        var ux = dx / len;
        var uy = dy / len;
        const double headSize = 14.0;
        var back = new Point(a.End.X - (ux * headSize), a.End.Y - (uy * headSize));
        var perpX = -uy;
        var perpY = ux;
        const double headWidth = 8.0;
        var p1 = new Point(back.X + (perpX * headWidth), back.Y + (perpY * headWidth));
        var p2 = new Point(back.X - (perpX * headWidth), back.Y - (perpY * headWidth));
        var fill = new SolidColorBrush(a.Stroke);
        dc.DrawGeometry(fill, pen, CreatePolyline(new[] { p1, a.End, p2 }, true));
    }

    private bool ShouldPreviewMosaic(MosaicAnnotation m)
    {
        if (ReferenceEquals(m, _livePreview))
        {
            return true;
        }

        // Select-tool move/resize of a committed mosaic while the mouse is down.
        if (_dragging
            && ActiveTool == ToolIds.Select
            && SelectedIndex is int idx
            && idx >= 0
            && idx < Items.Count
            && ReferenceEquals(Items[idx], m))
        {
            return true;
        }

        return false;
    }

    private void InvalidateMosaicPreview()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastMosaicInvalidateUtc).TotalMilliseconds < MosaicInvalidateMinMs)
        {
            return;
        }

        _lastMosaicInvalidateUtc = now;
        InvalidateVisual();
    }

    private bool IsMosaicDragInteraction()
    {
        if (ActiveTool == ToolIds.Mosaic)
        {
            return true;
        }

        return ActiveTool == ToolIds.Select
            && SelectedIndex is int idx
            && idx >= 0
            && idx < Items.Count
            && Items[idx] is MosaicAnnotation;
    }

    private void DrawMosaic(DrawingContext dc, MosaicAnnotation m, bool preview)
    {
        var src = SourceBitmap;
        int sw = src.PixelWidth;
        int sh = src.PixelHeight;
        var origin = SourceOffset;
        double scale = SourceDpiScale;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var rect = m.Rect;
        int x0 = Math.Max(0, (int)Math.Floor((rect.X + origin.X) * scale));
        int y0 = Math.Max(0, (int)Math.Floor((rect.Y + origin.Y) * scale));
        int x1 = Math.Min(sw, (int)Math.Ceiling((rect.Right + origin.X) * scale));
        int y1 = Math.Min(sh, (int)Math.Ceiling((rect.Bottom + origin.Y) * scale));
        int regionW = x1 - x0;
        int regionH = y1 - y0;
        if (regionW <= 0 || regionH <= 0)
        {
            return;
        }

        int bs = Math.Max(4, (int)Math.Round(m.BlockSize * scale));
        var mode = preview
            ? MosaicBlocks.SampleMode.Preview2x2
            : MosaicBlocks.SampleMode.FullAverage;

        EnsureMosaicOverlay(regionW, regionH);
        _mosaicOverlay.Lock();
        src.Lock();
        try
        {
            unsafe
            {
                MosaicBlocks.Fill(
                    (byte*)src.BackBuffer,
                    src.BackBufferStride,
                    sw,
                    sh,
                    (byte*)_mosaicOverlay.BackBuffer,
                    _mosaicOverlay.BackBufferStride,
                    x0,
                    y0,
                    x1,
                    y1,
                    bs,
                    mode);
            }

            _mosaicOverlay.AddDirtyRect(new Int32Rect(0, 0, regionW, regionH));
        }
        finally
        {
            src.Unlock();
            _mosaicOverlay.Unlock();
        }

        // Draw only the used sub-rect when the scratch bitmap is larger.
        var dest = new Rect(
            (x0 / scale) - origin.X,
            (y0 / scale) - origin.Y,
            regionW / scale,
            regionH / scale);
        var crop = new CroppedBitmap(_mosaicOverlay, new Int32Rect(0, 0, regionW, regionH));
        dc.DrawImage(crop, dest);
    }

    private void EnsureMosaicOverlay(int width, int height)
    {
        if (_mosaicOverlay != null
            && _mosaicOverlay.PixelWidth >= width
            && _mosaicOverlay.PixelHeight >= height
            && _mosaicOverlay.Format == PixelFormats.Bgra32)
        {
            return;
        }

        // Grow with a little slack so drag resize reallocates less often.
        int allocW = Math.Max(width, _mosaicOverlay?.PixelWidth ?? 0);
        int allocH = Math.Max(height, _mosaicOverlay?.PixelHeight ?? 0);
        allocW = Math.Max(allocW, width);
        allocH = Math.Max(allocH, height);
        _mosaicOverlay = new WriteableBitmap(allocW, allocH, 96, 96, PixelFormats.Bgra32, null);
    }

    /// <summary>Maps toolbar stroke width to mosaic tile size (thicker = coarser).</summary>
    private static int MosaicBlockSizeFromStroke(double strokeWidth)
        => Math.Clamp((int)Math.Round(strokeWidth * 4), 4, 48);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);

        if (_inlineTextBox != null)
        {
            CommitInlineText();
            return;
        }

        if (ActiveTool == ToolIds.Select)
        {
            BeginSelectEdit(p);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (ActiveTool == ToolIds.Text)
        {
            BeginInlineText(p, null);
            e.Handled = true;
            return;
        }

        _start = p;
        _dragging = true;
        if (ActiveTool == ToolIds.Pen)
        {
            _penPoints.Clear();
            _penPoints.Add(p);
            _livePreview = null;
        }
        else
        {
            _livePreview = CreateShapeAt(p, p);
        }

        CaptureMouse();
        e.Handled = true;
        base.OnMouseLeftButtonDown(e);
    }

    private void BeginSelectEdit(Point p)
    {
        if (SelectedIndex is int idx && idx >= 0 && idx < Items.Count)
        {
            var selected = Items[idx];
            var handle = AnnotationEditor.HitTestHandle(selected, p);
            if (handle is AnnotationEditor.HandleKind h && h != AnnotationEditor.HandleKind.Body)
            {
                _editHandle = h;
                _editStartPoint = p;
                _editSnapshot = selected;
                _editStartBounds = AnnotationEditor.GetBounds(selected);
                _dragging = true;
                return;
            }
        }

        var hit = AnnotationEditor.HitTestAnnotations(Items, p);
        if (hit >= 0)
        {
            SelectedIndex = hit;
            var now = DateTime.UtcNow;
            if (hit == _lastClickIndex && (now - _lastClickUtc).TotalMilliseconds < 450
                && Items[hit] is TextAnnotation)
            {
                _inlineTextIndex = hit;
                var text = Items[hit] as TextAnnotation;
                BeginInlineText(text.Origin, text.Text);
                return;
            }

            _lastClickIndex = hit;
            _lastClickUtc = now;

            _editHandle = AnnotationEditor.HandleKind.Body;
            _editStartPoint = p;
            _editSnapshot = Items[hit];
            _editStartBounds = AnnotationEditor.GetBounds(Items[hit]);
            _dragging = true;
            InvalidateVisual();
            return;
        }

        SelectedIndex = null;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging || !IsMouseCaptured)
        {
            return;
        }

        var p = e.GetPosition(this);

        if (ActiveTool == ToolIds.Select && SelectedIndex is int idx && _editSnapshot != null)
        {
            if (_editHandle == AnnotationEditor.HandleKind.Body)
            {
                var delta = p - _editStartPoint;
                Items[idx] = AnnotationEditor.Move(_editSnapshot, delta);
            }
            else if (_editHandle is AnnotationEditor.HandleKind handle)
            {
                Items[idx] = AnnotationEditor.ApplyHandle(_editSnapshot, handle, p, _editStartBounds);
            }

            if (IsMosaicDragInteraction())
            {
                InvalidateMosaicPreview();
            }
            else
            {
                InvalidateVisual();
            }

            return;
        }

        if (ActiveTool == ToolIds.Pen)
        {
            _penPoints.Add(p);
            if (_penPoints.Count >= 2)
            {
                _livePreview = new PenAnnotation
                {
                    Kind = AnnotationKind.Pen,
                    Stroke = CurrentStroke,
                    StrokeWidth = CurrentStrokeWidth,
                    Points = _penPoints.ToArray(),
                };
                InvalidateVisual();
            }

            return;
        }

        if (_start is Point s)
        {
            _livePreview = CreateShapeAt(s, p);
            if (ActiveTool == ToolIds.Mosaic)
            {
                InvalidateMosaicPreview();
            }
            else
            {
                InvalidateVisual();
            }
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (ActiveTool == ToolIds.Select)
        {
            _dragging = false;
            _editHandle = null;
            _editSnapshot = null;
            ReleaseMouseCapture();
            NotifyHistory();
            _lastMosaicInvalidateUtc = DateTime.MinValue;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var committed = _livePreview;
        _livePreview = null;
        _dragging = false;
        _start = null;
        _penPoints.Clear();
        ReleaseMouseCapture();
        if (committed != null)
        {
            Add(committed);
        }

        _lastMosaicInvalidateUtc = DateTime.MinValue;
        InvalidateVisual();
        e.Handled = true;
        base.OnMouseLeftButtonUp(e);
    }

    private Annotation CreateShapeAt(Point s, Point e)
    {
        var rect = NormalizeRect(s, e);
        return ActiveTool switch
        {
            ToolIds.Rect => new RectAnnotation
            {
                Kind = AnnotationKind.Rect,
                Stroke = CurrentStroke,
                StrokeWidth = CurrentStrokeWidth,
                Rect = rect,
            },
            ToolIds.Ellipse => new EllipseAnnotation
            {
                Kind = AnnotationKind.Ellipse,
                Stroke = CurrentStroke,
                StrokeWidth = CurrentStrokeWidth,
                Rect = rect,
            },
            ToolIds.Arrow => new ArrowAnnotation
            {
                Kind = AnnotationKind.Arrow,
                Stroke = CurrentStroke,
                StrokeWidth = CurrentStrokeWidth,
                Start = s,
                End = e,
            },
            ToolIds.Mosaic => new MosaicAnnotation
            {
                Kind = AnnotationKind.Mosaic,
                Stroke = CurrentStroke,
                StrokeWidth = CurrentStrokeWidth,
                Rect = rect,
                BlockSize = MosaicBlockSizeFromStroke(CurrentStrokeWidth),
            },
            _ => null,
        };
    }

    private static Rect NormalizeRect(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var w = Math.Max(1, Math.Abs(a.X - b.X));
        var h = Math.Max(1, Math.Abs(a.Y - b.Y));
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// Show an inline TextBox at the given position for creating or editing text.
    /// </summary>
    private void BeginInlineText(Point origin, string initialText)
    {
        if (_inlineTextBox != null)
        {
            return;
        }

        var fontSize = Math.Max(12, (CurrentStrokeWidth * 4) + 8);

        _inlineTextBox = new TextBox
        {
            Text = initialText ?? string.Empty,
            MinWidth = 60,
            MaxWidth = Math.Min(400, Math.Max(60, ActualWidth - origin.X - 8)),
            FontSize = fontSize,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4, 2, 4, 2),
            Foreground = new SolidColorBrush(CurrentStroke),
            CaretBrush = new SolidColorBrush(CurrentStroke),
            AcceptsReturn = false,
        };

        if (initialText != null)
        {
            _inlineTextBox.SelectAll();
            _inlineTextIndex = -1;
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i] is TextAnnotation ta && ta.Origin == origin && ta.Text == initialText)
                {
                    _inlineTextIndex = i;
                    break;
                }
            }
        }
        else
        {
            _inlineTextIndex = -1;
        }

        // Place the TextBox on the parent Canvas at the annotation coordinates.
        // The canvas is positioned at (Left, Top) inside the parent Canvas, so
        // that offset must be added to reach the correct screen position.
        var parent = Parent as Panel;
        if (parent == null)
        {
            return;
        }

        double parentX = Canvas.GetLeft(this);
        double parentY = Canvas.GetTop(this);
        _inlineTextBox.Tag = origin;
        parent.Children.Add(_inlineTextBox);
        Canvas.SetLeft(_inlineTextBox, origin.X + (double.IsNaN(parentX) ? 0 : parentX));
        Canvas.SetTop(_inlineTextBox, origin.Y + (double.IsNaN(parentY) ? 0 : parentY));
        Panel.SetZIndex(_inlineTextBox, 50);

        // Defer Focus() so the TextBox is attached to the visual tree first.
        Dispatcher.BeginInvoke(new Action(() => _inlineTextBox?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        _inlineTextBox.KeyDown += OnInlineTextBoxKeyDown;
        _inlineTextBox.LostFocus += OnInlineTextBoxLostFocus;
    }

    private void OnInlineTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            CommitInlineText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelInlineText();
            e.Handled = true;
        }
    }

    private void OnInlineTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        CommitInlineText();
    }

    private void CommitInlineText()
    {
        if (_inlineTextBox == null)
        {
            return;
        }

        var tb = _inlineTextBox;
        _inlineTextBox = null;
        var text = tb.Text ?? string.Empty;
        var origin = (Point)(tb.Tag ?? default(Point));
        var parent = Parent as Panel;
        if (parent != null)
        {
            parent.Children.Remove(tb);
        }

        if (_inlineTextIndex >= 0 && _inlineTextIndex < Items.Count)
        {
            // Editing existing text
            if (string.IsNullOrWhiteSpace(text))
            {
                Items.RemoveAt(_inlineTextIndex);
                SelectedIndex = null;
            }
            else if (Items[_inlineTextIndex] is TextAnnotation existing)
            {
                Items[_inlineTextIndex] = existing with { Text = text, Stroke = CurrentStroke };
            }

            _inlineTextIndex = -1;
            NotifyHistory();
            InvalidateVisual();
            return;
        }

        // Creating new text
        if (!string.IsNullOrWhiteSpace(text))
        {
            Add(new TextAnnotation
            {
                Kind = AnnotationKind.Text,
                Stroke = CurrentStroke,
                StrokeWidth = 0,
                Text = text,
                Origin = origin,
                FontSize = tb.FontSize,
            });
        }
    }

    private void CancelInlineText()
    {
        if (_inlineTextBox == null)
        {
            return;
        }

        var tb = _inlineTextBox;
        _inlineTextBox = null;
        _inlineTextIndex = -1;
        var parent = Parent as Panel;
        if (parent != null)
        {
            parent.Children.Remove(tb);
        }

        InvalidateVisual();
    }
}
