// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.UI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class PinImageTests
{
    [TestMethod]
    public void EditOutcomeIncludesPinned()
    {
        Assert.AreEqual("Pinned", EditOutcome.Pinned.ToString());
    }

    [TestMethod]
    public void ToolIconsPinGeometryIsDefined()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(ToolIcons.Pin));
        Assert.IsTrue(ToolIcons.Pin.Contains('M', System.StringComparison.Ordinal));
    }

    [TestMethod]
    public void ApplyZoomClampsAndSteps()
    {
        Assert.AreEqual(1.1, PinImageWindow.ApplyZoom(1.0, 120), 1e-9);
        Assert.AreEqual(1.0 / 1.1, PinImageWindow.ApplyZoom(1.0, -120), 1e-9);
        Assert.AreEqual(PinImageWindow.MinZoom, PinImageWindow.ApplyZoom(PinImageWindow.MinZoom, -120), 1e-9);
        Assert.AreEqual(PinImageWindow.MaxZoom, PinImageWindow.ApplyZoom(PinImageWindow.MaxZoom, 120), 1e-9);
    }

    [TestMethod]
    public void ApplyOpacityClampsAndSteps()
    {
        Assert.AreEqual(0.85, PinImageWindow.ApplyOpacity(0.8, 120), 1e-9);
        Assert.AreEqual(0.75, PinImageWindow.ApplyOpacity(0.8, -120), 1e-9);
        Assert.AreEqual(PinImageWindow.MinOpacity, PinImageWindow.ApplyOpacity(PinImageWindow.MinOpacity, -120), 1e-9);
        Assert.AreEqual(PinImageWindow.MaxOpacity, PinImageWindow.ApplyOpacity(PinImageWindow.MaxOpacity, 120), 1e-9);
    }
}
