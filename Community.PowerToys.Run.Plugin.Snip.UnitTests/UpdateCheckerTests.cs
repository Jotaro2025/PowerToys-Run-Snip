// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class UpdateCheckerTests
{
    [TestMethod]
    public void TryParseVersionStripsVAndPrerelease()
    {
        Assert.IsTrue(UpdateChecker.TryParseVersion("v1.4.0", out var a));
        Assert.AreEqual(new System.Version(1, 4, 0), a);
        Assert.IsTrue(UpdateChecker.TryParseVersion("1.4.1-beta", out var b));
        Assert.AreEqual(new System.Version(1, 4, 1), b);
        Assert.IsFalse(UpdateChecker.TryParseVersion("", out _));
    }

    [TestMethod]
    public void IsNewerComparesSemVerTags()
    {
        Assert.IsTrue(UpdateChecker.IsNewer("v1.5.0", "1.4.0"));
        Assert.IsFalse(UpdateChecker.IsNewer("v1.4.0", "1.4.0"));
        Assert.IsFalse(UpdateChecker.IsNewer("v1.3.9", "1.4.0"));
        Assert.IsTrue(UpdateChecker.IsNewer("2.0.0", "1.9.9"));
    }
}
