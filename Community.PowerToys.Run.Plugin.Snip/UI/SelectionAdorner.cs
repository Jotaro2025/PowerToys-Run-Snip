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

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Manages the selection border, size label, and 8 resize handles as direct
/// children of a host <see cref="Canvas"/>. Keeping handles as root-level
/// siblings (not inside a full-screen overlay) ensures they receive mouse
/// events while the annotation canvas below stays interactive.
/// </summary>
public sealed class SelectionAdorner
{
    public enum HandleKind
    {
        TopLeft,
        Top,
        TopRight,
        Left,
        Right,
        BottomLeft,
        Bottom,
        BottomRight,
    }

    public enum HandleDragState
    {
        Start,
        Move,
        End,
    }

    public sealed class HandleDragEventArgs : EventArgs
    {
        public HandleDragEventArgs(HandleKind kind, HandleDragState state, Point position)
        {
            Kind = kind;
            State = state;
            Position = position;
        }

        public HandleKind Kind { get; }

        public HandleDragState State { get; }

        public Point Position { get; }
    }

    private const double HandleSize = 10;

    private static readonly Color Chrome = Color.FromRgb(80, 220, 100);

    private readonly Canvas _host;
    private readonly Border _border;
    private readonly Border _labelHost;
    private readonly TextBlock _sizeLabel;
    private readonly Dictionary<HandleKind, Rectangle> _handles = new();

    public SelectionAdorner(Canvas host)
    {
        _host = host;

        _border = new Border
        {
            BorderBrush = new SolidColorBrush(Chrome),
            BorderThickness = new Thickness(1.5),
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        _host.Children.Add(_border);
        Panel.SetZIndex(_border, 15);

        _sizeLabel = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
        };
        _labelHost = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(4, 1, 4, 1),
            Child = _sizeLabel,
            IsHitTestVisible = false,
        };
        _host.Children.Add(_labelHost);
        Panel.SetZIndex(_labelHost, 15);

        foreach (HandleKind k in Enum.GetValues(typeof(HandleKind)))
        {
            var h = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Chrome),
                StrokeThickness = 1.5,
                IsHitTestVisible = true,
                Cursor = k switch
                {
                    HandleKind.TopLeft or HandleKind.BottomRight => Cursors.SizeNWSE,
                    HandleKind.TopRight or HandleKind.BottomLeft => Cursors.SizeNESW,
                    HandleKind.Top or HandleKind.Bottom => Cursors.SizeNS,
                    HandleKind.Left or HandleKind.Right => Cursors.SizeWE,
                    _ => Cursors.Arrow,
                },
            };
            h.MouseLeftButtonDown += (sender, e) =>
            {
                ((Rectangle)sender).CaptureMouse();
                OnHandleDrag(k, HandleDragState.Start, Mouse.GetPosition(_host));
                e.Handled = true;
            };
            h.MouseMove += (sender, e) =>
            {
                var rect = (Rectangle)sender;
                if (!rect.IsMouseCaptured)
                {
                    return;
                }

                OnHandleDrag(k, HandleDragState.Move, Mouse.GetPosition(_host));
                e.Handled = true;
            };
            h.MouseLeftButtonUp += (sender, e) =>
            {
                var rect = (Rectangle)sender;
                if (!rect.IsMouseCaptured)
                {
                    return;
                }

                OnHandleDrag(k, HandleDragState.End, Mouse.GetPosition(_host));
                rect.ReleaseMouseCapture();
                e.Handled = true;
            };
            _handles[k] = h;
            _host.Children.Add(h);
            Panel.SetZIndex(h, 20);
        }
    }

    public Rect Selection { get; set; }

    public bool IsVisible { get; set; }

    public event EventHandler<HandleDragEventArgs> HandleDrag;

    public void UpdateLayout()
    {
        var show = IsVisible && Selection.Width > 0 && Selection.Height > 0;
        _border.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _labelHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        foreach (var h in _handles.Values)
        {
            h.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!show)
        {
            return;
        }

        var s = Selection;
        _border.Width = s.Width;
        _border.Height = s.Height;
        Canvas.SetLeft(_border, s.X);
        Canvas.SetTop(_border, s.Y);

        _sizeLabel.Text = $"{(int)s.Width} x {(int)s.Height}";
        _labelHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var ls = _labelHost.DesiredSize;
        double labelY = s.Y - ls.Height - 2;
        if (labelY < 0)
        {
            labelY = s.Bottom + 2;
        }

        Canvas.SetLeft(_labelHost, s.X);
        Canvas.SetTop(_labelHost, labelY);

        // Handles sit outside the selection rect so they don't block drawing.
        var c = AnnotationEditor.Center(s);
        SetHandle(HandleKind.TopLeft, s.X - HandleSize, s.Y - HandleSize);
        SetHandle(HandleKind.Top, c.X - (HandleSize / 2), s.Y - HandleSize);
        SetHandle(HandleKind.TopRight, s.Right, s.Y - HandleSize);
        SetHandle(HandleKind.Left, s.X - HandleSize, c.Y - (HandleSize / 2));
        SetHandle(HandleKind.Right, s.Right, c.Y - (HandleSize / 2));
        SetHandle(HandleKind.BottomLeft, s.X - HandleSize, s.Bottom);
        SetHandle(HandleKind.Bottom, c.X - (HandleSize / 2), s.Bottom);
        SetHandle(HandleKind.BottomRight, s.Right, s.Bottom);
    }

    private void SetHandle(HandleKind k, double x, double y)
    {
        var h = _handles[k];
        Canvas.SetLeft(h, x);
        Canvas.SetTop(h, y);
    }

    private void OnHandleDrag(HandleKind k, HandleDragState s, Point p)
        => HandleDrag?.Invoke(this, new HandleDragEventArgs(k, s, p));
}
