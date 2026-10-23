// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Community.PowerToys.Run.Plugin.Snip.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class CaptureKindTests
{
    [TestMethod]
    public void CaptureKindValuesAreStable()
    {
        Assert.AreEqual(0, (int)CaptureKind.Region);
        Assert.AreEqual(1, (int)CaptureKind.ActiveWindow);
        Assert.AreEqual(2, (int)CaptureKind.FullScreen);
    }
}
