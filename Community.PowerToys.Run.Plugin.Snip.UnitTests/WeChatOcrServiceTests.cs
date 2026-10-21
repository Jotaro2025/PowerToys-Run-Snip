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
}
