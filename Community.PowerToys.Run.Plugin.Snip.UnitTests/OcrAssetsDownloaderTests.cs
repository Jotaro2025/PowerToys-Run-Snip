// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

    [TestMethod]
    public void ExpectedSha256IsLowercaseHexDigest()
    {
        Assert.AreEqual(64, OcrAssetsDownloader.ExpectedSha256.Length);
        Assert.IsTrue(OcrAssetsDownloader.ExpectedSha256.All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c)));
    }

    [TestMethod]
    public async Task EnsureDownloadedRejectsHashMismatchAndKeepsCacheEmpty()
    {
        var temp = Path.Combine(Path.GetTempPath(), "snip-ocr-sha-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(temp, "wco_data");
        Directory.CreateDirectory(temp);
        try
        {
            byte[] nupkg = BuildFakeNupkg();
            using var client = new HttpClient(new BytesHandler(nupkg));
            var (ok, path, error) = await OcrAssetsDownloader.EnsureDownloadedAsync(
                client,
                cache,
                new string('0', 64),
                [("mirror-a", "https://example.invalid/a.nupkg"), ("mirror-b", "https://example.invalid/b.nupkg")],
                force: true);

            Assert.IsFalse(ok);
            Assert.IsNull(path);
            StringAssert.Contains(error, "SHA-256");
            StringAssert.Contains(error, "mirror-b");
            Assert.IsFalse(Directory.Exists(cache), "Rejected download must not populate the cache.");
            Assert.AreEqual(0, Directory.GetFiles(temp, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    [TestMethod]
    public async Task EnsureDownloadedAcceptsMatchingHash()
    {
        var temp = Path.Combine(Path.GetTempPath(), "snip-ocr-sha-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(temp, "wco_data");
        Directory.CreateDirectory(temp);
        try
        {
            byte[] nupkg = BuildFakeNupkg();
            string hash = Convert.ToHexStringLower(SHA256.HashData(nupkg));
            using var client = new HttpClient(new BytesHandler(nupkg));
            var (ok, path, error) = await OcrAssetsDownloader.EnsureDownloadedAsync(
                client,
                cache,
                hash.ToUpperInvariant(),
                [("mirror-a", "https://example.invalid/a.nupkg")],
                force: true);

            Assert.IsTrue(ok, error);
            Assert.AreEqual(cache, path);
            CollectionAssert.AreEqual(Array.Empty<string>(), WeChatOcrService.FindMissingFiles(cache).ToArray());
        }
        finally
        {
            TryDelete(temp);
        }
    }

    [TestMethod]
    public async Task VerifyNupkgThrowsWithSourceNameOnMismatch()
    {
        var file = Path.Combine(Path.GetTempPath(), "snip-ocr-verify-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        try
        {
            var ex = await Assert.ThrowsExceptionAsync<InvalidDataException>(
                () => OcrAssetsDownloader.VerifyNupkgAsync(file, OcrAssetsDownloader.ExpectedSha256, "nuget.org", CancellationToken.None));
            StringAssert.Contains(ex.Message, "nuget.org");
            StringAssert.Contains(ex.Message, OcrAssetsDownloader.ExpectedSha256);

            string good = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 }));
            await OcrAssetsDownloader.VerifyNupkgAsync(file, good, "nuget.org", CancellationToken.None);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static byte[] BuildFakeNupkg()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in new[]
            {
                "content/wco_data/WeChatOCR.exe",
                "content/wco_data/mmmojo_64.dll",
                "content/wco_data/Model/OCRRecogFP32V1.1.0.26.xnet",
            })
            {
                using var w = new StreamWriter(zip.CreateEntry(entry).Open(), Encoding.UTF8);
                w.Write(entry);
            }
        }

        return ms.ToArray();
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
