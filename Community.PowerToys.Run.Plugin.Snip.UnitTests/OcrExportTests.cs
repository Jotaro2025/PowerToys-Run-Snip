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
public class OcrExportTests
{
    [TestMethod]
    public void Markdown_RendersRowPerLineWithGeometry()
    {
        var lines = new[]
        {
            new OcrTextLine("Hello", new Rect(10, 20, 100, 12), 0.98f),
            new OcrTextLine("World", new Rect(10, 40, 80, 12), 0.75f),
        };

        string md = OcrExport.Render(lines, OcrExport.Format.Markdown);

        Assert.IsTrue(md.Contains("| 1 | Hello | 0.98 | 10 | 20 | 100 | 12 |"), md);
        Assert.IsTrue(md.Contains("| 2 | World | 0.75 | 10 | 40 | 80 | 12 |"), md);
        Assert.IsTrue(md.Contains("| # | Text | Conf | X | Y | W | H |"), md);
    }

    [TestMethod]
    public void Markdown_EscapesPipesAndNewlines()
    {
        var lines = new[] { new OcrTextLine("a|b\nc", new Rect(0, 0, 9, 9), 1f) };
        string md = OcrExport.Render(lines, OcrExport.Format.Markdown);
        Assert.IsTrue(md.Contains("a\\|b<br>c"), md);
    }

    [TestMethod]
    public void Json_RendersTextBoxAndCorners()
    {
        System.Collections.Generic.IReadOnlyList<Point> corners =
            new[] { new Point(1, 2), new Point(3, 4), new Point(5, 6), new Point(7, 8) };
        var lines = new[] { new OcrTextLine("say \"hi\"", new Rect(5, 6, 70, 20), 0.9f, corners) };

        string json = OcrExport.Render(lines, OcrExport.Format.Json);

        Assert.IsTrue(json.Contains("\"text\": \"say \\\"hi\\\"\""), json);
        Assert.IsTrue(json.Contains("\"box\": { \"x\": 5, \"y\": 6, \"w\": 70, \"h\": 20 }"), json);
        Assert.IsTrue(json.Contains("\"corners\": [{ \"x\": 1, \"y\": 2 }, { \"x\": 3, \"y\": 4 }, { \"x\": 5, \"y\": 6 }, { \"x\": 7, \"y\": 8 } ]"), json);
    }

    [TestMethod]
    public void Json_EmptyList_RendersEmptyArray()
    {
        string json = OcrExport.Render(Array.Empty<OcrTextLine>(), OcrExport.Format.Json);
        StringAssert.Contains(json, "[");
        StringAssert.Contains(json, "]");
    }
}
