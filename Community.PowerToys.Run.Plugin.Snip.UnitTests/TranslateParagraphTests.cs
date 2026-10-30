// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class TranslateParagraphTests
{
    [TestMethod]
    public void StackedLinesWithTightGapBecomeOneParagraph()
    {
        var lines = new[]
        {
            Line("one", 10, 10, 120, 18),
            Line("two", 10, 32, 110, 18),
            Line("three", 10, 54, 100, 18),
        };

        var paragraphs = TranslateParagraph.Group(lines);

        Assert.AreEqual(1, paragraphs.Count);
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, paragraphs[0].LineIndices.ToArray());
        Assert.AreEqual(10, paragraphs[0].Box.Top, 0.01);
        Assert.AreEqual(72, paragraphs[0].Box.Bottom, 0.01);
        Assert.AreEqual(18, paragraphs[0].LineHeight, 0.01);
    }

    [TestMethod]
    public void WideGapStartsNewParagraph()
    {
        var lines = new[]
        {
            Line("one", 10, 10, 120, 18),
            Line("two", 10, 32, 110, 18),
            Line("far", 10, 120, 110, 18),
        };

        var paragraphs = TranslateParagraph.Group(lines);

        Assert.AreEqual(2, paragraphs.Count);
        Assert.AreEqual(2, paragraphs[0].LineIndices.Count);
        Assert.AreEqual(1, paragraphs[1].LineIndices.Count);
    }

    [TestMethod]
    public void SideBySideColumnsStaySeparate()
    {
        var lines = new[]
        {
            Line("left one", 10, 10, 80, 18),
            Line("right one", 120, 10, 80, 18),
            Line("left two", 10, 32, 80, 18),
            Line("right two", 120, 32, 80, 18),
        };

        var paragraphs = TranslateParagraph.Group(lines);

        Assert.AreEqual(2, paragraphs.Count);
        Assert.IsTrue(paragraphs.All(p => p.LineIndices.Count == 2));
        Assert.IsTrue(paragraphs[0].Box.Right <= 90, "left column paragraph must not include the right column");
        Assert.IsTrue(paragraphs[1].Box.Left >= 120, "right column paragraph must not include the left column");
    }

    [TestMethod]
    public void HeadingWithDifferentHeightStaysSeparate()
    {
        var lines = new[]
        {
            Line("Title", 10, 10, 160, 34),
            Line("body", 10, 50, 160, 16),
        };

        var paragraphs = TranslateParagraph.Group(lines);

        Assert.AreEqual(2, paragraphs.Count);
    }

    [TestMethod]
    public void QuadLineNeverJoinsAParagraph()
    {
        var corners = new[]
        {
            new Point(10, 10),
            new Point(70, 10),
            new Point(70, 28),
            new Point(10, 28),
        };
        var quad = new OcrTextLine("rot", new Rect(10, 10, 60, 18), 1f, corners);
        var body = Line("body", 10, 32, 60, 18);

        var paragraphs = TranslateParagraph.Group(new[] { quad, body });

        Assert.AreEqual(2, paragraphs.Count);
    }

    [TestMethod]
    public void CenteredLinesAreDetected()
    {
        var lines = new[]
        {
            Line("wide line", 10, 10, 200, 18),
            Line("narrow", 85, 32, 50, 18),
        };

        var paragraphs = TranslateParagraph.Group(lines);

        Assert.AreEqual(1, paragraphs.Count);
        Assert.AreEqual(ParagraphAlignment.Center, paragraphs[0].Alignment);
    }

    [TestMethod]
    public void RightAlignedLinesAreDetected()
    {
        var lines = new[]
        {
            Line("value one", 60, 10, 150, 18),
            Line("value two", 40, 32, 170, 18),
        };

        var paragraphs = TranslateParagraph.Group(lines);

        Assert.AreEqual(1, paragraphs.Count);
        Assert.AreEqual(ParagraphAlignment.Right, paragraphs[0].Alignment);
    }

    [TestMethod]
    public void LeftAlignedLinesAreDetected()
    {
        var lines = new[]
        {
            Line("first", 10, 10, 120, 18),
            Line("second longer", 10, 32, 160, 18),
        };

        var paragraphs = TranslateParagraph.Group(lines);

        Assert.AreEqual(1, paragraphs.Count);
        Assert.AreEqual(ParagraphAlignment.Left, paragraphs[0].Alignment);
    }

    [TestMethod]
    public void JoinTranslationPartsUsesSpacesOnlyForNonCjkBoundaries()
    {
        Assert.AreEqual("Hello world", ImageTranslateRenderer.JoinTranslationParts(new[] { "Hello", "world" }));
        Assert.AreEqual("你好世界", ImageTranslateRenderer.JoinTranslationParts(new[] { "你好", "世界" }));
        Assert.AreEqual("你好 world", ImageTranslateRenderer.JoinTranslationParts(new[] { "你好", "world" }));
        Assert.AreEqual("Hello 世界", ImageTranslateRenderer.JoinTranslationParts(new[] { "Hello", "世界" }));
        Assert.AreEqual("a", ImageTranslateRenderer.JoinTranslationParts(new[] { " ", "a", "" }));
    }

    private static OcrTextLine Line(string text, double left, double top, double width, double height)
        => new OcrTextLine(text, new Rect(left, top, width, height), 1f);
}
