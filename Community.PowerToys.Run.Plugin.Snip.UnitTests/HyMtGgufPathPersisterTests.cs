// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class HyMtGgufPathPersisterTests
{
    [TestMethod]
    public void TryNormalizeHttpsUrlAcceptsHttpsOnly()
    {
        Assert.IsTrue(HyMtModelDownloader.TryNormalizeHttpsUrl(
            "https://example.com/a.gguf", out var url, out _));
        Assert.AreEqual("https://example.com/a.gguf", url);

        Assert.IsFalse(HyMtModelDownloader.TryNormalizeHttpsUrl("http://example.com/a.gguf", out _, out _));
        Assert.IsFalse(HyMtModelDownloader.TryNormalizeHttpsUrl("ftp://example.com/a.gguf", out _, out _));
        Assert.IsFalse(HyMtModelDownloader.TryNormalizeHttpsUrl("not-a-url", out _, out _));
        Assert.IsFalse(HyMtModelDownloader.TryNormalizeHttpsUrl(" ", out _, out _));
    }

    [TestMethod]
    public void BuildDownloadSourcesPutsCustomFirstWhenHttps()
    {
        var sources = HyMtModelDownloader.BuildDownloadSources("https://mirror.example/model.gguf");
        Assert.AreEqual("Custom", sources[0].Name);
        Assert.AreEqual("https://mirror.example/model.gguf", sources[0].Url);
        Assert.AreEqual("ModelScope", sources[1].Name);
        Assert.AreEqual(4, sources.Length);
    }

    [TestMethod]
    public void BuildDownloadSourcesIgnoresInvalidCustom()
    {
        var sources = HyMtModelDownloader.BuildDownloadSources("http://insecure.example/x.gguf");
        Assert.AreEqual(3, sources.Length);
        Assert.AreEqual("ModelScope", sources[0].Name);
    }

    [TestMethod]
    public void TryPatchPowerToysRunSettingsUpdatesTextValue()
    {
        var path = Path.Combine(Path.GetTempPath(), "snip-pt-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var initial = """
                {
                  "name": "PowerToys Run",
                  "plugins": [
                    {
                      "Id": "7667E8C5E74948F7B2D6B5E7CED60783",
                      "Name": "Snip",
                      "AdditionalOptions": [
                        { "Key": "HyMtGgufPath", "TextValue": "" },
                        { "Key": "DownloadRecommendedHyMtModel", "Value": true }
                      ]
                    }
                  ]
                }
                """;
            File.WriteAllText(path, initial);

            Assert.IsTrue(HyMtGgufPathPersister.TryPatchPowerToysRunSettings(@"D:\models\Hy.gguf", path));

            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var options = root["plugins"]![0]!["AdditionalOptions"]!.AsArray();
            var pathOpt = options.First(o => o!["Key"]!.GetValue<string>() == "HyMtGgufPath");
            Assert.AreEqual(@"D:\models\Hy.gguf", pathOpt!["TextValue"]!.GetValue<string>());
            var dl = options.First(o => o!["Key"]!.GetValue<string>() == "DownloadRecommendedHyMtModel");
            Assert.IsFalse(dl!["Value"]!.GetValue<bool>());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
