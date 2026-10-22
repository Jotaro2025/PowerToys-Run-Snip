// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class OcrAssetsDownloaderTests
{
    [TestMethod]
    public void TryGetWcoRelativePathAcceptsContentPrefix()
    {
        var rel = OcrAssetsDownloader.TryGetWcoRelativePath("content/wco_data/WeChatOCR.exe");
        Assert.AreEqual("WeChatOCR.exe", rel?.Replace('\\', '/'));
    }

    [TestMethod]
    public void TryGetWcoRelativePathAcceptsContentFilesFallback()
    {
        var rel = OcrAssetsDownloader.TryGetWcoRelativePath(
            "contentFiles/any/net8.0-windows7.0/wco_data/Model/RecDict");
        Assert.AreEqual(Path.Combine("Model", "RecDict"), rel);
    }

    [TestMethod]
    public void TryGetWcoRelativePathRejectsUnrelatedEntries()
    {
        Assert.IsNull(OcrAssetsDownloader.TryGetWcoRelativePath("lib/net8.0-windows7.0/WeChatOcr.dll"));
        Assert.IsNull(OcrAssetsDownloader.TryGetWcoRelativePath(null));
    }

    [TestMethod]
    public void ExtractWcoDataWritesRequiredLayout()
    {
        var temp = Path.Combine(Path.GetTempPath(), "snip-ocr-extract-" + Guid.NewGuid().ToString("N"));
        var nupkg = Path.Combine(temp, "fake.nupkg");
        var dest = Path.Combine(temp, "wco_data");
        Directory.CreateDirectory(temp);

        try
        {
            using (var zip = ZipFile.Open(nupkg, ZipArchiveMode.Create))
            {
                void Add(string entryPath, string text)
                {
                    var e = zip.CreateEntry(entryPath);
                    using var w = new StreamWriter(e.Open(), Encoding.UTF8);
                    w.Write(text);
                }

                Add("content/wco_data/WeChatOCR.exe", "exe");
                Add("content/wco_data/mmmojo_64.dll", "dll");
                Add("content/wco_data/Model/OCRRecogFP32V1.1.0.26.xnet", "model");
                Add("content/wco_data/x64.config", "x64");
                // Duplicate contentFiles entry should be ignored when content/ already provided,
                // but Extract writes both paths to the same relative file (overwrite OK).
                Add("lib/net8.0/WeChatOcr.dll", "should-skip");
            }

            OcrAssetsDownloader.ExtractWcoData(nupkg, dest);

            Assert.IsTrue(File.Exists(Path.Combine(dest, "WeChatOCR.exe")));
            Assert.IsTrue(File.Exists(Path.Combine(dest, "mmmojo_64.dll")));
            Assert.IsTrue(File.Exists(Path.Combine(dest, "Model", "OCRRecogFP32V1.1.0.26.xnet")));
            Assert.IsFalse(File.Exists(Path.Combine(dest, "WeChatOcr.dll")));
            CollectionAssert.AreEqual(
                Array.Empty<string>(),
                WeChatOcrService.FindMissingFiles(dest).ToArray());
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [TestMethod]
    public void PackageVersionMatchesKnownWeChatOcrReference()
    {
        // Guardrail: keep OcrAssetsDownloader.PackageVersion synced with csproj.
        Assert.AreEqual("1.0.5", OcrAssetsDownloader.PackageVersion);
        Assert.AreEqual("wechatocr", OcrAssetsDownloader.PackageId);
        StringAssert.Contains(OcrAssetsDownloader.NugetOrgUrl, OcrAssetsDownloader.PackageVersion);
    }
}
