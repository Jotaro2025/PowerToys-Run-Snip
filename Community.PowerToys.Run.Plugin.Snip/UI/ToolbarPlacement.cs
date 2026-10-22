// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;

namespace Community.PowerToys.Run.Plugin.Snip.UI;

/// <summary>
/// Positions the floating annotation toolbar so it stays inside the capture
/// viewport (one screen's logical bounds), preferring below the selection.
/// </summary>
public static class ToolbarPlacement
{
    /// <summary>
    /// Computes the top-left of the toolbar in the same coordinate space as
    /// <paramref name="selection"/> and <paramref name="viewport"/>.
    /// </summary>
    public static Point Compute(
        Rect selection,
        Size toolbarSize,
        Size viewport,
        double margin = 8,
        double gap = 12)
    {
        if (toolbarSize.Width < 1)
        {
            toolbarSize.Width = 1;
        }

        if (toolbarSize.Height < 1)
        {
            toolbarSize.Height = 1;
        }

        double maxX = Math.Max(margin, viewport.Width - toolbarSize.Width - margin);
        double maxY = Math.Max(margin, viewport.Height - toolbarSize.Height - margin);

        double x = selection.X + (selection.Width / 2) - (toolbarSize.Width / 2);
        double yBelow = selection.Bottom + gap;
        double yAbove = selection.Y - toolbarSize.Height - gap;

        double y;
        bool belowFits = yBelow + toolbarSize.Height <= viewport.Height - margin;
        bool aboveFits = yAbove >= margin;
        if (belowFits)
        {
            y = yBelow;
        }
        else if (aboveFits)
        {
            y = yAbove;
        }
        else
        {
            // Neither side has full room: clamp into the viewport.
            y = Math.Clamp(yBelow, margin, maxY);
        }

        x = Math.Clamp(x, margin, maxX);
        y = Math.Clamp(y, margin, maxY);
        return new Point(x, y);
    }
}
