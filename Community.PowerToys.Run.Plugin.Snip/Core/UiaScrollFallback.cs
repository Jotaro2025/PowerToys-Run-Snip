// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;
using System.Windows.Automation;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// UI Automation <see cref="ScrollPattern"/> fallback when SendInput / WM_MOUSEWHEEL
/// cannot move the underlay (some elevated or non-Chromium hosts).
/// Prefer calling from an STA (UI dispatcher).
/// </summary>
internal static class UiaScrollFallback
{
    /// <summary>
    /// Tries to scroll the UI Automation element at a physical screen point.
    /// Positive <paramref name="delta"/> scrolls up (content moves down); negative scrolls down.
    /// </summary>
    public static bool TryScrollAt(int physicalX, int physicalY, int delta)
    {
        if (delta == 0)
        {
            return false;
        }

        try
        {
            var point = new Point(physicalX, physicalY);
            AutomationElement element = AutomationElement.FromPoint(point);
            if (element == null)
            {
                return false;
            }

            ScrollPattern scroll = FindScrollPattern(element);
            if (scroll == null)
            {
                Log.Info(
                    $"Snip scroll UIA: no ScrollPattern at ({physicalX},{physicalY})",
                    typeof(UiaScrollFallback));
                return false;
            }

            bool vertical = scroll.Current.VerticallyScrollable;
            bool horizontal = scroll.Current.HorizontallyScrollable;
            if (!vertical && !horizontal)
            {
                return false;
            }

            // Match wheel: positive delta → scroll up (SmallDecrement on vertical).
            ScrollAmount amount = delta > 0 ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement;
            if (vertical)
            {
                scroll.Scroll(ScrollAmount.NoAmount, amount);
            }
            else
            {
                scroll.Scroll(amount, ScrollAmount.NoAmount);
            }

            Log.Info(
                $"Snip scroll UIA ScrollPattern at ({physicalX},{physicalY}) delta={delta} vertical={vertical}",
                typeof(UiaScrollFallback));
            return true;
        }
        catch (ElementNotAvailableException ex)
        {
            Log.Warn($"Snip scroll UIA element gone: {ex.Message}", typeof(UiaScrollFallback));
            return false;
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn($"Snip scroll UIA invalid op: {ex.Message}", typeof(UiaScrollFallback));
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip scroll UIA failed: {ex.Message}", typeof(UiaScrollFallback));
            return false;
        }
    }

    private static ScrollPattern FindScrollPattern(AutomationElement start)
    {
        AutomationElement current = start;
        for (int depth = 0; depth < 12 && current != null; depth++)
        {
            if (current.TryGetCurrentPattern(ScrollPattern.Pattern, out object pattern)
                && pattern is ScrollPattern scroll)
            {
                return scroll;
            }

            try
            {
                current = TreeWalker.ControlViewWalker.GetParent(current);
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        return null;
    }
}
