// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;

using Community.PowerToys.Run.Plugin.Snip.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class AnnotationCanvasHitTestTests
{
    [TestMethod]
    public void EmptyCanvasCoversItsWholeSurfaceForHitTesting()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var canvas = new AnnotationCanvas
                {
                    Width = 400,
                    Height = 300,
                    IsHitTestVisible = true,
                    Visibility = Visibility.Visible,
                };

                canvas.Measure(new Size(400, 300));
                canvas.Arrange(new Rect(0, 0, 400, 300));

                // A custom FrameworkElement is only hit-testable where it draws.
                // Without the transparent full-surface rectangle the first press
                // inside the selection fell through to the input catcher and no
                // rectangle/ellipse/pen annotation could ever start.
                var hit = VisualTreeHelper.HitTest(canvas, new Point(200, 150));
                Assert.IsNotNull(hit, "empty annotation canvas must be hit-testable");
                Assert.AreSame(canvas, hit.VisualHit);
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
