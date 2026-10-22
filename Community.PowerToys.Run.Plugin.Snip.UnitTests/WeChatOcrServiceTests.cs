// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WeChatOcr;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class WeChatOcrServiceTests
{
    [TestMethod]
    public void BundledOcrComponentIsCopiedNextToPlugin()
    {
        // The WeChatOcr package copies wco_data\ into the build output of the
        // plugin (and of this test project, which references it).
        var dataDirectory = WeChatOcrService.OcrDataDirectory;

        Assert.IsTrue(Directory.Exists(dataDirectory), dataDirectory);
        CollectionAssert.AreEqual(Array.Empty<string>(), WeChatOcrService.FindMissingFiles(dataDirectory).ToArray());
    }

    [TestMethod]
    public void FindMissingFilesReportsIncompleteComponent()
    {
        var missing = WeChatOcrService.FindMissingFiles(Path.Combine(Path.GetTempPath(), "snip-no-ocr-" + Guid.NewGuid().ToString("N"))).ToList();

        CollectionAssert.Contains(missing, "WeChatOCR.exe");
        CollectionAssert.Contains(missing, "mmmojo_64.dll");
    }

    [TestMethod]
    public void JoinLinesKeepsRecognizedLinesInOrder()
    {
        var result = new WeChatOcrResult
        {
            OcrResult = new OcrResult
            {
                SingleResult = new List<SingleResult>
                {
                    new SingleResult { SingleStrUtf8 = "第一行" },
                    new SingleResult { SingleStrUtf8 = string.Empty },
                    new SingleResult { SingleStrUtf8 = "second line" },
                },
            },
        };

        Assert.AreEqual("第一行" + Environment.NewLine + "second line", WeChatOcrService.JoinLines(result));
    }

    [TestMethod]
    public void JoinLinesReturnsEmptyWithoutText()
    {
        Assert.AreEqual(string.Empty, WeChatOcrService.JoinLines(null));
        Assert.AreEqual(string.Empty, WeChatOcrService.JoinLines(new WeChatOcrResult()));
    }

    [TestMethod]
    public void ExtractLinesKeepsBoxesAndReadingOrder()
    {
        var result = new WeChatOcrResult
        {
            OcrResult = new OcrResult
            {
                SingleResult = new List<SingleResult>
                {
                    new SingleResult { SingleStrUtf8 = "bottom", Left = 10, Top = 80, Right = 50, Bottom = 100, SingleRate = 0.9f },
                    new SingleResult { SingleStrUtf8 = "top", Left = 5, Top = 10, Right = 40, Bottom = 30, SingleRate = 0.95f },
                    new SingleResult { SingleStrUtf8 = string.Empty, Left = 0, Top = 0, Right = 1, Bottom = 1 },
                },
            },
        };

        var lines = WeChatOcrService.ExtractLines(result);
        Assert.HasCount(2, lines);
        Assert.AreEqual("top", lines[0].Text);
        Assert.AreEqual("bottom", lines[1].Text);
        Assert.AreEqual(5, lines[0].Box.X, 0.01);
        Assert.AreEqual(20, lines[0].Box.Height, 0.01);
    }
}
