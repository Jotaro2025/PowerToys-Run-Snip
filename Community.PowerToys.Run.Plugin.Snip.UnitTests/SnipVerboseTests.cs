// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Community.PowerToys.Run.Plugin.Snip;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public sealed class SnipVerboseTests
{
    [TestMethod]
    public void Scroll_WhenDisabled_DoesNotThrow()
    {
        var prev = SnipVerbose.ScrollLogging;
        try
        {
            SnipVerbose.ScrollLogging = false;
            SnipVerbose.Scroll("[stitch] test quiet");
        }
        finally
        {
            SnipVerbose.ScrollLogging = prev;
        }
    }

    [TestMethod]
    public void Scroll_WhenEnabled_DoesNotThrow()
    {
        var prev = SnipVerbose.ScrollLogging;
        try
        {
            SnipVerbose.ScrollLogging = true;
            SnipVerbose.Scroll("[scroll] test verbose");
        }
        finally
        {
            SnipVerbose.ScrollLogging = prev;
        }
    }

    [TestMethod]
    public void Settings_DefaultVerboseOff()
    {
        Assert.IsFalse(new SnipSettings().VerboseScrollLogging);
    }
}
