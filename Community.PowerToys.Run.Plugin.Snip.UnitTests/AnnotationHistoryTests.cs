// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;

using Community.PowerToys.Run.Plugin.Snip.Core.Annotations;
using Community.PowerToys.Run.Plugin.Snip.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class AnnotationHistoryTests
{
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

    [TestMethod]
    public void UndoThenRedoRestoresAnnotation()
    {
        RunOnSta(() =>
        {
            var canvas = new AnnotationCanvas();
            var rect = new RectAnnotation
            {
                Kind = AnnotationKind.Rect,
                Stroke = Colors.Red,
                StrokeWidth = 2,
                Rect = new Rect(1, 2, 30, 10),
            };
            canvas.Add(rect);
            Assert.AreEqual(1, canvas.UndoDepth);
            Assert.AreEqual(0, canvas.RedoDepth);

            Assert.IsTrue(canvas.Undo());
            Assert.AreEqual(0, canvas.UndoDepth);
            Assert.AreEqual(1, canvas.RedoDepth);
            Assert.AreEqual(0, canvas.Items.Count);

            Assert.IsTrue(canvas.Redo());
            Assert.AreEqual(1, canvas.UndoDepth);
            Assert.AreEqual(0, canvas.RedoDepth);
            Assert.AreEqual(1, canvas.Items.Count);
        });
    }

    [TestMethod]
    public void NewAddClearsRedoStack()
    {
        RunOnSta(() =>
        {
            var canvas = new AnnotationCanvas();
            canvas.Add(new RectAnnotation
            {
                Kind = AnnotationKind.Rect,
                Stroke = Colors.Red,
                StrokeWidth = 1,
                Rect = new Rect(0, 0, 5, 5),
            });
            canvas.Undo();
            Assert.AreEqual(1, canvas.RedoDepth);

            canvas.Add(new RectAnnotation
            {
                Kind = AnnotationKind.Rect,
                Stroke = Colors.Blue,
                StrokeWidth = 1,
                Rect = new Rect(2, 2, 5, 5),
            });
            Assert.AreEqual(0, canvas.RedoDepth);
            Assert.IsFalse(canvas.Redo());
        });
    }

    [TestMethod]
    public void ComputeNextStepNumberUsesMaxPlusOneAfterIntermediateDelete()
    {
        var items = new Annotation[]
        {
            new StepAnnotation { Kind = AnnotationKind.Step, Stroke = Colors.Red, StrokeWidth = 2, Center = new Point(10, 10), Number = 1, Radius = 12 },
            new StepAnnotation { Kind = AnnotationKind.Step, Stroke = Colors.Red, StrokeWidth = 2, Center = new Point(30, 10), Number = 3, Radius = 12 },
        };

        Assert.AreEqual(4, AnnotationCanvas.ComputeNextStepNumber(items));
        Assert.AreEqual(1, AnnotationCanvas.ComputeNextStepNumber(Array.Empty<Annotation>()));
    }

    [TestMethod]
    public void ConstrainShapeEndpointLocksSquareAnd45DegreeAngles()
    {
        var s = new Point(100, 100);
        var sq = AnnotationCanvas.ConstrainShapeEndpoint(Community.PowerToys.Run.Plugin.Snip.Models.ToolIds.Rect, s, new Point(150, 120), shiftPressed: true);
        Assert.AreEqual(150, sq.X, 1e-6);
        Assert.AreEqual(150, sq.Y, 1e-6);

        var horiz = AnnotationCanvas.ConstrainShapeEndpoint(Community.PowerToys.Run.Plugin.Snip.Models.ToolIds.Line, s, new Point(200, 108), shiftPressed: true);
        Assert.AreEqual(100, horiz.Y, 1e-4);
        Assert.IsTrue(horiz.X > 190);

        var diag = AnnotationCanvas.ConstrainShapeEndpoint(Community.PowerToys.Run.Plugin.Snip.Models.ToolIds.Arrow, s, new Point(160, 155), shiftPressed: true);
        Assert.AreEqual(Math.Abs(diag.X - s.X), Math.Abs(diag.Y - s.Y), 1e-4);
    }
}
