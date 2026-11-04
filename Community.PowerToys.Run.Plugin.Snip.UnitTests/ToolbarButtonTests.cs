// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;

using Community.PowerToys.Run.Plugin.Snip.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

/// <summary>
/// ToolbarButton must raise Click on mouse release. When it fired from the
/// mouse-down handler, an action that opened a modal dialog (the "translation
/// engine not configured" warning) swallowed the physical mouse-up, so the
/// capture taken after the handler stuck and every later click was routed back
/// to that same button.
/// </summary>
[TestClass]
public class ToolbarButtonTests
{
    [TestMethod]
    public void ClickFiresOnReleaseNotOnPress()
    {
        RunOnSta(() =>
        {
            var button = new ToolbarButton();
            int clicks = 0;
            button.Click += (_, _) => clicks++;

            RaiseMouseButton(button, UIElement.MouseLeftButtonDownEvent);
            Assert.AreEqual(0, clicks, "Click must not fire on mouse down");

            RaiseMouseButton(button, UIElement.MouseLeftButtonUpEvent);
            Assert.AreEqual(1, clicks, "Click must fire on mouse up");
        });
    }

    [TestMethod]
    public void ReleasingOutsideCancelsTheClick()
    {
        RunOnSta(() =>
        {
            var button = new ToolbarButton();
            int clicks = 0;
            button.Click += (_, _) => clicks++;

            RaiseMouseButton(button, UIElement.MouseLeftButtonDownEvent);
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = UIElement.MouseLeaveEvent,
            });
            RaiseMouseButton(button, UIElement.MouseLeftButtonUpEvent);

            Assert.AreEqual(0, clicks);
        });
    }

    private static void RaiseMouseButton(UIElement target, RoutedEvent routedEvent)
    {
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = routedEvent,
        });
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
