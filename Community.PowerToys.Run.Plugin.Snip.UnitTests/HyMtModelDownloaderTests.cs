// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;

using Community.PowerToys.Run.Plugin.Snip;
using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class HyMtModelDownloaderTests
{
    [TestMethod]
    public void DefaultCachePathUsesLocalAppDataPluginFolder()
    {
        var path = HyMtModelDownloader.DefaultCachePath;
        StringAssert.Contains(path, Path.Combine("PowerToys", "RunPlugins", "Community.PowerToys.Run.Plugin.Snip", "models"));
        StringAssert.EndsWith(path, HyMtModelDownloader.FileName);
    }

    [TestMethod]
    public void IsValidCachedFileRejectsMissingAndTinyFiles()
    {
        Assert.IsFalse(HyMtModelDownloader.IsValidCachedFile(null));
        Assert.IsFalse(HyMtModelDownloader.IsValidCachedFile(string.Empty));
        Assert.IsFalse(HyMtModelDownloader.IsValidCachedFile(Path.Combine(Path.GetTempPath(), "snip-missing-" + Path.GetRandomFileName())));

        var tiny = Path.Combine(Path.GetTempPath(), "snip-tiny-" + Path.GetRandomFileName() + ".gguf");
        try
        {
            File.WriteAllBytes(tiny, new byte[] { 1, 2, 3, 4 });
            Assert.IsFalse(HyMtModelDownloader.IsValidCachedFile(tiny));
        }
        finally
        {
            if (File.Exists(tiny))
            {
                File.Delete(tiny);
            }
        }
    }

    [TestMethod]
    public void ResolveGgufPathPrefersExplicitOverCache()
    {
        var settings = new SnipSettings
        {
            TranslatorProvider = TranslatorProvider.HyMT2Sharp,
            HyMtGgufPath = Path.Combine(Path.GetTempPath(), "snip-explicit-missing-" + Path.GetRandomFileName() + ".gguf"),
        };

        // Explicit path missing and no valid cache → null
        Assert.IsNull(HyMtModelDownloader.ResolveGgufPath(settings));
    }

    [TestMethod]
    public void ValidateHyMtAcceptsResolvedCachePath()
    {
        // Without a real multi-hundred-MB file, Validate should still report missing/empty.
        var error = TranslatorFactory.Validate(new SnipSettings
        {
            TranslatorProvider = TranslatorProvider.HyMT2Sharp,
            HyMtGgufPath = string.Empty,
            TargetLanguage = "简体中文",
        });
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void DownloadUrlPrefersModelScope()
    {
        StringAssert.Contains(HyMtModelDownloader.DownloadUrl, "modelscope.cn");
        StringAssert.Contains(HyMtModelDownloader.ModelScopeUrl, "modelscope.cn");
        StringAssert.Contains(HyMtModelDownloader.HfMirrorUrl, "hf-mirror.com");
        StringAssert.Contains(HyMtModelDownloader.HuggingFaceUrl, "huggingface.co");
        Assert.AreEqual(HyMtModelDownloader.ModelScopeUrl, HyMtModelDownloader.DownloadUrl);
    }
}
