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
}
