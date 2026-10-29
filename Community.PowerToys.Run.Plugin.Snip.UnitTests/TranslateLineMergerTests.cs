// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class TranslateLineMergerTests
{
    [TestMethod]
    public void MergesFragmentsOnTheSameBaseline()
    {
        var lines = new[]
        {
            new OcrTextLine("NativeAOT", new Rect(10, 100, 80, 20), 1f),
            new OcrTextLine("runs", new Rect(95, 100, 40, 20), 1f),
        };

        var merged = TranslateLineMerger.Merge(lines);

        Assert.AreEqual(1, merged.Count);
        Assert.AreEqual("NativeAOT runs", merged[0].Text);
        Assert.AreEqual(10, merged[0].Box.Left, 0.01);
        Assert.AreEqual(135, merged[0].Box.Right, 0.01);
    }

    [TestMethod]
    public void CollapsesFragmentInsideTheLineBox()
    {
        // Inline-code fragment reported inside the full-line box: the stray 1–2
        // character translation painted there is what looked like a black dot.
        var lines = new[]
        {
            new OcrTextLine("the runtime implements it", new Rect(10, 100, 400, 20), 1f),
            new OcrTextLine("1", new Rect(300, 102, 6, 16), 0.4f),
        };

        var merged = TranslateLineMerger.Merge(lines);

        Assert.AreEqual(1, merged.Count);
        StringAssert.Contains(merged[0].Text, "the runtime implements it");
        StringAssert.Contains(merged[0].Text, "1");
    }

    [TestMethod]
    public void KeepsFarApartColumnsSeparate()
    {
        var lines = new[]
        {
            new OcrTextLine("left column", new Rect(10, 100, 100, 20), 1f),
            new OcrTextLine("right column", new Rect(600, 100, 100, 20), 1f),
        };

        Assert.AreEqual(2, TranslateLineMerger.Merge(lines).Count);
    }

    [TestMethod]
    public void KeepsDifferentBaselinesSeparate()
    {
        var lines = new[]
        {
            new OcrTextLine("first", new Rect(10, 100, 100, 20), 1f),
            new OcrTextLine("second", new Rect(10, 140, 100, 20), 1f),
        };

        Assert.AreEqual(2, TranslateLineMerger.Merge(lines).Count);
    }

    [TestMethod]
    public void DropsPunctuationOnlyLines()
    {
        var lines = new[]
        {
            new OcrTextLine("|", new Rect(10, 100, 4, 20), 0.3f),
            new OcrTextLine("·", new Rect(20, 100, 4, 20), 0.3f),
            new OcrTextLine("——", new Rect(30, 100, 30, 20), 0.3f),
            new OcrTextLine("A", new Rect(80, 100, 12, 20), 0.9f),
        };

        var merged = TranslateLineMerger.Merge(lines);

        Assert.AreEqual(1, merged.Count);
        Assert.AreEqual("A", merged[0].Text);
    }

    [TestMethod]
    public void CollapsesDuplicateBoxesOnce()
    {
        var lines = new[]
        {
            new OcrTextLine("duplicate text", new Rect(10, 100, 120, 20), 1f),
            new OcrTextLine("duplicate text", new Rect(10, 100, 120, 20), 1f),
        };

        var merged = TranslateLineMerger.Merge(lines);

        Assert.AreEqual(1, merged.Count);
        Assert.AreEqual("duplicate text", merged[0].Text);
    }

    [TestMethod]
    public void KeepsCjkFragmentsTight()
    {
        Assert.AreEqual("中文内容", TranslateLineMerger.JoinText("中文", "内容"));
        Assert.AreEqual("中文NativeAOT", TranslateLineMerger.JoinText("中文", "NativeAOT"));
        Assert.AreEqual("a b", TranslateLineMerger.JoinText("a", "b"));
    }

    [TestMethod]
    public void MergePreservesReadingOrder()
    {
        var lines = new[]
        {
            new OcrTextLine("second line", new Rect(10, 140, 120, 20), 1f),
            new OcrTextLine("first line", new Rect(10, 100, 120, 20), 1f),
        };

        var merged = TranslateLineMerger.Merge(lines);

        Assert.AreEqual("first line", merged[0].Text);
        Assert.AreEqual("second line", merged[1].Text);
    }
}
