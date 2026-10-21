// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

using Community.PowerToys.Run.Plugin.Snip.Core.Annotations;
using Community.PowerToys.Run.Plugin.Snip.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class AnnotationEditorTests
{
    private static RectAnnotation Rect(double x, double y, double w, double h)
        => new RectAnnotation
        {
            Kind = AnnotationKind.Rect,
            Stroke = Colors.Red,
            StrokeWidth = 2,
            Rect = new Rect(x, y, w, h),
        };

    private static T Cast<T>(object value)
    {
        Assert.IsInstanceOfType(value, typeof(T));
        return (T)value;
    }

    [TestMethod]
    public void GetBoundsInflatesByStrokeWidth()
    {
        var bounds = AnnotationEditor.GetBounds(Rect(10, 20, 100, 50));

        Assert.AreEqual(new Rect(8, 18, 104, 54), bounds);
    }

    [TestMethod]
    public void GetBoundsForPenCoversAllPoints()
    {
        var pen = new PenAnnotation
        {
            Kind = AnnotationKind.Pen,
            Stroke = Colors.Red,
            StrokeWidth = 2,
            Points = new[] { new Point(5, 5), new Point(30, 40), new Point(10, 60) },
        };

        var bounds = AnnotationEditor.GetBounds(pen);

        Assert.AreEqual(new Rect(3, 3, 29, 59), bounds);
    }

    [TestMethod]
    public void HitTestFindsTopmostAnnotationFirst()
    {
        var first = Rect(0, 0, 100, 100);
        var second = Rect(50, 50, 100, 100);
        var items = new Annotation[] { first, second };

        Assert.AreEqual(1, AnnotationEditor.HitTestAnnotations(items, new Point(60, 60)));
        Assert.AreEqual(0, AnnotationEditor.HitTestAnnotations(items, new Point(10, 10)));
        Assert.AreEqual(-1, AnnotationEditor.HitTestAnnotations(items, new Point(400, 400)));
    }

    [TestMethod]
    public void HitTestRespectsTolerance()
    {
        var rect = Rect(100, 100, 50, 50);

        Assert.IsTrue(AnnotationEditor.HitTest(rect, new Point(96, 100)));
        Assert.IsFalse(AnnotationEditor.HitTest(rect, new Point(80, 100)));
    }

    [TestMethod]
    public void HitTestHandleReturnsCornerHandles()
    {
        var rect = Rect(100, 100, 50, 50);

        Assert.AreEqual(AnnotationEditor.HandleKind.TopLeft, AnnotationEditor.HitTestHandle(rect, new Point(100, 100)));
        Assert.AreEqual(AnnotationEditor.HandleKind.BottomRight, AnnotationEditor.HitTestHandle(rect, new Point(150, 150)));
        Assert.AreEqual(AnnotationEditor.HandleKind.Body, AnnotationEditor.HitTestHandle(rect, new Point(120, 120)));
        Assert.IsNull(AnnotationEditor.HitTestHandle(rect, new Point(400, 400)));
    }

    [TestMethod]
    public void MoveOffsetsTheWholeAnnotation()
    {
        var moved = AnnotationEditor.Move(Rect(10, 10, 20, 20), new Vector(5, -3));

        var rect = Cast<RectAnnotation>(moved);
        Assert.AreEqual(new Rect(15, 7, 20, 20), rect.Rect);
    }

    [TestMethod]
    public void ApplyHandleResizesFromTopLeft()
    {
        var start = Rect(10, 10, 40, 40);
        var bounds = AnnotationEditor.GetBounds(start);

        var resized = AnnotationEditor.ApplyHandle(start, AnnotationEditor.HandleKind.TopLeft, new Point(0, 0), bounds);

        var rect = Cast<RectAnnotation>(resized);
        Assert.AreEqual(new Rect(0, 0, 50, 50), rect.Rect);
    }

    [TestMethod]
    public void ApplyHandleNormalizesInvertedDrags()
    {
        var start = Rect(10, 10, 40, 40);
        var bounds = AnnotationEditor.GetBounds(start);

        // Drag the top-left handle far past the bottom-right corner.
        var resized = AnnotationEditor.ApplyHandle(start, AnnotationEditor.HandleKind.TopLeft, new Point(200, 200), bounds);

        var rect = Cast<RectAnnotation>(resized);

        // The handle was dragged past the opposite corner, so the rect is
        // normalized: the new origin is the old bottom-right corner.
        Assert.AreEqual(50, rect.Rect.X);
        Assert.AreEqual(50, rect.Rect.Y);
        Assert.AreEqual(150, rect.Rect.Width);
        Assert.AreEqual(150, rect.Rect.Height);
    }

    [TestMethod]
    public void ArrowHandleMovesEndPoint()
    {
        var arrow = new ArrowAnnotation
        {
            Kind = AnnotationKind.Arrow,
            Stroke = Colors.Red,
            StrokeWidth = 2,
            Start = new Point(0, 0),
            End = new Point(10, 10),
        };

        var moved = AnnotationEditor.ApplyHandle(arrow, AnnotationEditor.HandleKind.EndPoint, new Point(30, 5), default);

        var result = Cast<ArrowAnnotation>(moved);
        Assert.AreEqual(new Point(30, 5), result.End);
        Assert.AreEqual(new Point(0, 0), result.Start);
    }

    [TestMethod]
    public void GetHandleCentersReturnsEightForRect()
    {
        var centers = AnnotationEditor.GetHandleCenters(Rect(0, 0, 100, 100)).ToList();

        Assert.HasCount(8, centers);
    }

    [TestMethod]
    public void CenterMatchesRectMiddle()
    {
        Assert.AreEqual(new Point(50, 25), AnnotationEditor.Center(new Rect(0, 0, 100, 50)));
    }

    [TestMethod]
    public void SupportsResizeMatchesAnnotationKind()
    {
        Assert.IsTrue(AnnotationEditor.SupportsResize(Rect(0, 0, 10, 10)));
        Assert.IsFalse(AnnotationEditor.SupportsResize(new ArrowAnnotation
        {
            Kind = AnnotationKind.Arrow,
            Stroke = Colors.Red,
            StrokeWidth = 2,
            Start = default,
            End = default,
        }));
    }

    [TestMethod]
    public void ShallowCopyCreatesIndependentPointsList()
    {
        var pen = new PenAnnotation
        {
            Kind = AnnotationKind.Pen,
            Stroke = Colors.Red,
            StrokeWidth = 2,
            Points = new[] { new Point(1, 1), new Point(2, 2) },
        };

        var copy = Cast<PenAnnotation>(pen.ShallowCopy());

        Assert.AreNotSame(pen.Points, copy.Points);
        Assert.AreEqual(pen.Points.Count, copy.Points.Count);
    }
}
