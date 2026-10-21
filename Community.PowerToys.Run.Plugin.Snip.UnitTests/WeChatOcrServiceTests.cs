// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WeChatOcr;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class WeChatOcrServiceTests
{
    private string _root;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "snip-wechat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_root, true);
    }

    private static string CreateWeChatVersionDir(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "mmmojo.dll"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(dir, "mmmojo_64.dll"), Array.Empty<byte>());
        return dir;
    }

    [TestMethod]
    public void ResolveWeChatDirPicksNewestVersionFolder()
    {
        CreateWeChatVersionDir(_root, "[3.9.9.43]");
        var newest = CreateWeChatVersionDir(_root, "[3.9.12.51]");
        Directory.CreateDirectory(Path.Combine(_root, "[3.9.13.1]")); // no mmmojo: ignored

        Assert.AreEqual(newest, WeChatOcrService.ResolveWeChatDir(_root));
    }

    [TestMethod]
    public void ResolveWeChatDirAcceptsVersionFolderItself()
    {
        var dir = CreateWeChatVersionDir(_root, "[3.9.12.51]");

        Assert.AreEqual(dir, WeChatOcrService.ResolveWeChatDir("\"" + dir + Path.DirectorySeparatorChar + "\""));
    }

    [TestMethod]
    public void ResolveWeChatDirRejectsFoldersWithoutWeChat()
    {
        Assert.IsNull(WeChatOcrService.ResolveWeChatDir(_root));
        Assert.IsNull(WeChatOcrService.ResolveWeChatDir(Path.Combine(_root, "missing")));
        Assert.IsNull(WeChatOcrService.ResolveWeChatDir("   "));
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
