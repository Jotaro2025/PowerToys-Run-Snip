// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public sealed class UiaScrollFallbackTests
{
    [TestMethod]
    public void TryScrollAt_ZeroDelta_ReturnsFalse()
    {
        Assert.IsFalse(UiaScrollFallback.TryScrollAt(0, 0, delta: 0));
    }

    [TestMethod]
    public void TryScrollAt_OffScreen_DoesNotThrow()
    {
        // Far off-screen: may return false or fail soft; must not throw out.
        _ = UiaScrollFallback.TryScrollAt(-100000, -100000, delta: -120);
    }
}
