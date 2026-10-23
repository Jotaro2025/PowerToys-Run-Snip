// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class ProcessIntegrityTests
{
    [TestMethod]
    public void IsHigherThanCurrent_ZeroHwnd_ReturnsFalse()
    {
        Assert.IsFalse(ProcessIntegrity.IsHigherThanCurrent(IntPtr.Zero));
    }

    [TestMethod]
    public void TryGetIntegrityRid_ZeroPid_ReturnsFalse()
    {
        Assert.IsFalse(ProcessIntegrity.TryGetIntegrityRid(0, out int rid, out bool denied));
        Assert.AreEqual(0, rid);
        Assert.IsFalse(denied);
    }
}
