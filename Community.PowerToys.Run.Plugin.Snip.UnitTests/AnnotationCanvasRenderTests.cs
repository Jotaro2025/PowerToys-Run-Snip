// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Community.PowerToys.Run.Plugin.Snip.Core.Annotations;
using Community.PowerToys.Run.Plugin.Snip.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

/// <summary>
/// Renders one annotation of every kind. Two things are guarded here:
/// pens/brushes are shared frozen instances (mutating one throws during render),
/// and WPF only strokes the segments a StreamGeometry marks as stroked, so a
/// geometry built the wrong way silently paints nothing.
/// </summary>
[TestClass]
public class AnnotationCanvasRenderTests
{
    private const int Width = 400;
    private const int Height = 300;

    [TestMethod]
    public void EveryAnnotationKindRendersThroughLayout()
    {
        RunOnSta(() =>
        {
            var canvas = BuildCanvasWithEveryKind();

            // Mirrors the host's failing stack: arrange -> OnRender -> DrawAnnotation.
            canvas.Measure(new Size(Width, Height));
            canvas.Arrange(new Rect(0, 0, Width, Height));
        });
    }

    [TestMethod]
    public void EveryAnnotationKindRendersForExport()
    {
        RunOnSta(() =>
        {
            var canvas = BuildCanvasWithEveryKind();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                canvas.RenderAnnotations(dc);
            }
        });
    }

    /// <summary>
    /// A kind that exists in <c>Items</c> but paints no pixels is invisible to the
    /// user — exactly the failure mode of a stroke-less polyline geometry.
    /// </summary>
    [TestMethod]
    public void EveryAnnotationKindPaintsVisiblePixels()
    {
        RunOnSta(() =>
        {
            foreach (var (name, annotation) in OneOfEveryKind())
            {
                var canvas = new AnnotationCanvas
                {
                    Width = Width,
                    Height = Height,
                    SourceBitmap = CreateSourceBitmap(),
                };
                canvas.Items.Add(annotation);

                var host = new Border
                {
                    Width = Width,
                    Height = Height,
                    Background = Brushes.White,
                    Child = canvas,
                };
                host.Measure(new Size(Width, Height));
                host.Arrange(new Rect(0, 0, Width, Height));
                host.UpdateLayout();

                var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);

                Assert.IsTrue(CountNonWhitePixels(bitmap) > 0, $"{name} painted no visible pixels");
            }
        });
    }

    private static AnnotationCanvas BuildCanvasWithEveryKind()
    {
        var canvas = new AnnotationCanvas
        {
            Width = Width,
            Height = Height,
            SourceBitmap = CreateSourceBitmap(),
            IsHitTestVisible = true,
            Visibility = Visibility.Visible,
        };

        foreach (var (_, annotation) in OneOfEveryKind())
        {
            canvas.Items.Add(annotation);
        }

        return canvas;
    }

    private static IEnumerable<(string Name, Annotation Annotation)> OneOfEveryKind()
    {
        var stroke = Color.FromRgb(238, 32, 77);
        const double width = 3;

        yield return ("Pen", new PenAnnotation
        {
            Kind = AnnotationKind.Pen,
            Stroke = stroke,
            StrokeWidth = width,
            Points = new List<Point> { new(10, 10), new(40, 30), new(70, 12) },
        });
        yield return ("Line", new LineAnnotation
        {
            Kind = AnnotationKind.Line,
            Stroke = stroke,
            StrokeWidth = width,
            Start = new Point(90, 10),
            End = new Point(140, 60),
        });
        yield return ("Arrow", new ArrowAnnotation
        {
            Kind = AnnotationKind.Arrow,
            Stroke = stroke,
            StrokeWidth = width,
            Start = new Point(150, 10),
            End = new Point(200, 70),
        });
        yield return ("Rect", new RectAnnotation
        {
            Kind = AnnotationKind.Rect,
            Stroke = stroke,
            StrokeWidth = width,
            Rect = new Rect(210, 10, 60, 40),
        });
        yield return ("Ellipse", new EllipseAnnotation
        {
            Kind = AnnotationKind.Ellipse,
            Stroke = stroke,
            StrokeWidth = width,
            Rect = new Rect(280, 10, 60, 40),
        });
        yield return ("Highlight", new HighlightAnnotation
        {
            Kind = AnnotationKind.Highlight,
            Stroke = Colors.Yellow,
            StrokeWidth = width,
            Rect = new Rect(10, 100, 120, 30),
        });
        yield return ("Step", new StepAnnotation
        {
            Kind = AnnotationKind.Step,
            Stroke = stroke,
            StrokeWidth = width,
            Center = new Point(160, 115),
            Number = 1,
            Radius = 14,
        });
        yield return ("Text", new TextAnnotation
        {
            Kind = AnnotationKind.Text,
            Stroke = stroke,
            StrokeWidth = width,
            Text = "text",
            Origin = new Point(200, 100),
            FontSize = 18,
        });
        yield return ("Mosaic", new MosaicAnnotation
        {
            Kind = AnnotationKind.Mosaic,
            Stroke = stroke,
            StrokeWidth = width,
            Rect = new Rect(10, 160, 100, 60),
            BlockSize = 12,
        });
    }

    private static WriteableBitmap CreateSourceBitmap()
    {
        // Dark source so a mosaic over it is visible against the white backdrop.
        var bitmap = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[Width * Height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 40;
            pixels[i + 1] = 40;
            pixels[i + 2] = 40;
            pixels[i + 3] = 255;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), pixels, Width * 4, 0);
        return bitmap;
    }

    private static int CountNonWhitePixels(BitmapSource bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        int painted = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 240 || pixels[i + 1] < 240 || pixels[i + 2] < 240)
            {
                painted++;
            }
        }

        return painted;
    }

    private static void RunOnSta(Action action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw failure;
        }
    }
}
