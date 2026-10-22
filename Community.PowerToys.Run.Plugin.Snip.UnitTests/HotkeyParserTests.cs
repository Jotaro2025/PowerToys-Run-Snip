// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Windows.Input;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class HotkeyParserTests
{
    [TestMethod]
    public void EmptyStringIsDisabledSuccess()
    {
        Assert.IsTrue(HotkeyParser.TryParse(string.Empty, out var mods, out var vk, out var error));
        Assert.AreEqual(0u, mods);
        Assert.AreEqual(0u, vk);
        Assert.IsNull(error);

        Assert.IsTrue(HotkeyParser.TryParse("   ", out mods, out vk, out error));
        Assert.AreEqual(0u, mods);
    }

    [TestMethod]
    public void ParsesCtrlShiftS()
    {
        Assert.IsTrue(HotkeyParser.TryParse("Ctrl+Shift+S", out var mods, out var vk, out var error));
        Assert.IsNull(error);
        Assert.AreEqual(HotkeyParser.ModControl | HotkeyParser.ModShift, mods);
        Assert.AreEqual((uint)KeyInterop.VirtualKeyFromKey(Key.S), vk);
    }

    [TestMethod]
    public void RoundTripsFormat()
    {
        Assert.IsTrue(HotkeyParser.TryParse("control+alt+f12", out var mods, out var vk, out _));
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        var formatted = HotkeyParser.Format(mods, key);
        Assert.AreEqual("Ctrl+Alt+F12", formatted);
        Assert.AreEqual("Ctrl+Alt+F12", HotkeyParser.Normalize("control+alt+f12"));
    }

    [TestMethod]
    public void RejectsKeyWithoutModifier()
    {
        Assert.IsFalse(HotkeyParser.TryParse("S", out _, out _, out var error));
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    public void RejectsModifiersOnly()
    {
        Assert.IsFalse(HotkeyParser.TryParse("Ctrl+Shift", out _, out _, out var error));
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    public void RejectsTwoKeys()
    {
        Assert.IsFalse(HotkeyParser.TryParse("Ctrl+A+B", out _, out _, out _));
    }

    [TestMethod]
    public void ParsesWinKey()
    {
        Assert.IsTrue(HotkeyParser.TryParse("Win+Shift+A", out var mods, out _, out _));
        Assert.AreEqual(HotkeyParser.ModWin | HotkeyParser.ModShift, mods);
    }
}
