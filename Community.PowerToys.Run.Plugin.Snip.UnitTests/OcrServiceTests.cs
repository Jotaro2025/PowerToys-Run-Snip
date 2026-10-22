// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Linq;
using System.Runtime.InteropServices;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class OcrServiceTests
{
    [TestMethod]
    [DataRow(OcrEngineKind.Auto, true, OcrEngineKind.WeChat)]
    [DataRow(OcrEngineKind.Auto, false, OcrEngineKind.Windows)]
    [DataRow(OcrEngineKind.WeChat, false, OcrEngineKind.WeChat)]
    [DataRow(OcrEngineKind.WeChat, true, OcrEngineKind.WeChat)]
    [DataRow(OcrEngineKind.Windows, true, OcrEngineKind.Windows)]
    public void ResolveEngineOnX64(OcrEngineKind setting, bool weChatAssets, OcrEngineKind expected)
    {
        Assert.AreEqual(expected, OcrService.ResolveEngine(setting, Architecture.X64, weChatAssets));
    }

    [TestMethod]
    [DataRow(OcrEngineKind.Auto, true)]
    [DataRow(OcrEngineKind.WeChat, true)]
    [DataRow(OcrEngineKind.Windows, false)]
    public void ResolveEngineOnArm64IsAlwaysWindows(OcrEngineKind setting, bool weChatAssets)
    {
        Assert.AreEqual(OcrEngineKind.Windows, OcrService.ResolveEngine(setting, Architecture.Arm64, weChatAssets));
        Assert.AreEqual(OcrEngineKind.Windows, OcrService.ResolveEngine(setting, Architecture.X86, weChatAssets));
    }

    [TestMethod]
    public void JoinWordsSpacesLatinButNotCjk()
    {
        Assert.AreEqual("Hello world", WindowsOcrService.JoinWords(["Hello", "world"]));
        Assert.AreEqual("你好世界", WindowsOcrService.JoinWords(["你", "好", "世", "界"]));
        Assert.AreEqual("下载PowerToys最新版", WindowsOcrService.JoinWords(["下", "载", "PowerToys", "最", "新", "版"]));
        Assert.AreEqual("こんにちは", WindowsOcrService.JoinWords(["こ", "ん", "に", "ち", "は"]));
        Assert.AreEqual("a b", WindowsOcrService.JoinWords(["a", string.Empty, null, "b"]));
    }

    [TestMethod]
    public void PlanStripsSingleWhenFits()
    {
        var strips = WindowsOcrService.PlanStrips(800, 10000, 256);
        Assert.HasCount(1, strips);
        Assert.AreEqual(new WindowsOcrService.OcrStrip(0, 800, 0, 800), strips[0]);
    }

    [TestMethod]
    [DataRow(25000, 10000, 256)]
    [DataRow(10001, 10000, 256)]
    [DataRow(31234, 4000, 300)]
    public void PlanStripsCoverAllRowsWithDisjointKeepRanges(int height, int max, int overlap)
    {
        var strips = WindowsOcrService.PlanStrips(height, max, overlap);

        Assert.IsTrue(strips.Count > 1);
        Assert.IsTrue(strips.All(s => s.Height <= max && s.Height > 0));
        Assert.AreEqual(0, strips[0].Top);
        Assert.AreEqual(height, strips[^1].Top + strips[^1].Height);
        Assert.AreEqual(0, strips[0].KeepTop);
        Assert.AreEqual(height, strips[^1].KeepBottom);

        for (int i = 0; i < strips.Count; i++)
        {
            var s = strips[i];

            // Keep range lies inside the strip, so every kept line was fully visible to the engine (up to overlap/2).
            Assert.IsTrue(s.KeepTop >= s.Top && s.KeepBottom <= s.Top + s.Height, $"strip {i}");
            if (i > 0)
            {
                // Strips overlap, and keep ranges tile the image with no gap or double count.
                Assert.IsTrue(s.Top < strips[i - 1].Top + strips[i - 1].Height, $"overlap {i}");
                Assert.AreEqual(strips[i - 1].KeepBottom, s.KeepTop, $"keep seam {i}");
            }
        }
    }
}
