// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class Win32WindowRegionTests
{
    [TestMethod]
    public void TryClampHole_ClampsAndRejectsEmpty()
    {
        Assert.IsTrue(Win32WindowRegion.TryClampHole(
            100, 80, 10, 20, 30, 40,
            out int left, out int top, out int right, out int bottom));
        Assert.AreEqual(10, left);
        Assert.AreEqual(20, top);
        Assert.AreEqual(40, right);
        Assert.AreEqual(60, bottom);

        Assert.IsFalse(Win32WindowRegion.TryClampHole(
            100, 80, 50, 50, 0, 10,
            out _, out _, out _, out _));
    }

    [TestMethod]
    public void TryClampHole_FullClientIsValidClamp()
    {
        Assert.IsTrue(Win32WindowRegion.TryClampHole(
            200, 100, 0, 0, 200, 100,
            out int left, out int top, out int right, out int bottom));
        Assert.AreEqual(0, left);
        Assert.AreEqual(0, top);
        Assert.AreEqual(200, right);
        Assert.AreEqual(100, bottom);
    }

    [TestMethod]
    public void ApplySelectionHole_RejectsZeroHwndAndFullClient()
    {
        Assert.IsFalse(Win32WindowRegion.ApplySelectionHole(IntPtr.Zero, 100, 100, 10, 10, 20, 20));
        // Full-client hole must not SetWindowRgn to an empty region (白屏).
        Assert.IsFalse(Win32WindowRegion.ApplySelectionHole(new IntPtr(1), 100, 100, 0, 0, 100, 100));
    }
}
