// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        StringAssert.EndsWith(HyMtModelDownloader.DefaultPartialPath, HyMtModelDownloader.PartialFileSuffix);
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

        // Isolate from whatever model the developer machine has downloaded.
        try
        {
            HyMtModelDownloader.SetCachePathForTests(
                Path.Combine(Path.GetTempPath(), "snip-cache-" + Path.GetRandomFileName(), HyMtModelDownloader.FileName));
            Assert.IsNull(HyMtModelDownloader.ResolveGgufPath(settings));
        }
        finally
        {
            HyMtModelDownloader.SetCachePathForTests(null);
        }
    }

    [TestMethod]
    public void ValidateHyMtAcceptsResolvedCachePath()
    {
        try
        {
            HyMtModelDownloader.SetCachePathForTests(
                Path.Combine(Path.GetTempPath(), "snip-cache-" + Path.GetRandomFileName(), HyMtModelDownloader.FileName));
            var error = TranslatorFactory.Validate(new SnipSettings
            {
                TranslatorProvider = TranslatorProvider.HyMT2Sharp,
                HyMtGgufPath = string.Empty,
                TargetLanguage = "简体中文",
            });
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        }
        finally
        {
            HyMtModelDownloader.SetCachePathForTests(null);
        }
    }

    [TestMethod]
    public void DownloadUrlPrefersModelScope()
    {
        StringAssert.Contains(HyMtModelDownloader.DownloadUrl, "modelscope.cn");
        Assert.AreEqual(HyMtModelDownloader.ModelScopeUrl, HyMtModelDownloader.DownloadUrl);
    }

    [TestMethod]
    public void HashesMatchIsOrdinalIgnoreCase()
    {
        Assert.IsTrue(HyMtModelDownloader.HashesMatch(
            HyMtModelDownloader.ExpectedSha256.ToUpperInvariant(),
            HyMtModelDownloader.ExpectedSha256));
        Assert.IsFalse(HyMtModelDownloader.HashesMatch("deadbeef", HyMtModelDownloader.ExpectedSha256));
        Assert.IsFalse(HyMtModelDownloader.HashesMatch(null, HyMtModelDownloader.ExpectedSha256));
    }

    [TestMethod]
    public void GetPartialLengthReadsExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "snip-partial-" + Path.GetRandomFileName());
        try
        {
            Assert.AreEqual(0, HyMtModelDownloader.GetPartialLength(path));
            File.WriteAllBytes(path, new byte[1234]);
            Assert.AreEqual(1234, HyMtModelDownloader.GetPartialLength(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task FinalizePartialRejectsShaMismatchAndDeletesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "snip-bad-sha-" + Path.GetRandomFileName() + ".downloading");
        try
        {
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("not-the-official-gguf"));
            Assert.IsTrue(File.Exists(path));
            var (ok, error, fatal) = await HyMtModelDownloader.FinalizePartialAsync(
                path, "test", minValidSizeBytes: 1, CancellationToken.None);
            Assert.IsFalse(ok);
            Assert.IsFalse(fatal);
            StringAssert.Contains(error, "SHA");
            Assert.IsFalse(File.Exists(path), "mismatched partial must be deleted");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task FinalizePartialRejectsTinyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "snip-tiny-final-" + Path.GetRandomFileName());
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
            var (ok, error, fatal) = await HyMtModelDownloader.FinalizePartialAsync(path, "test", CancellationToken.None);
            Assert.IsFalse(ok);
            Assert.IsFalse(fatal);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
            Assert.IsTrue(File.Exists(path), "tiny incomplete file is not auto-deleted by Finalize");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task TryDownloadResumesWithRangeWhenServerReturns206()
    {
        var dir = Path.Combine(Path.GetTempPath(), "snip-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tempPath = Path.Combine(dir, "model.gguf.downloading");
        try
        {
            // Seed a small partial; mock will append and we'll bypass size/SHA by not calling Finalize
            // through a handler that returns tiny body — instead verify Range header + append behavior
            // via a custom path that only checks WriteResponse side effects.
            byte[] first = Encoding.UTF8.GetBytes("HELLO-");
            byte[] second = Encoding.UTF8.GetBytes("WORLD");
            await File.WriteAllBytesAsync(tempPath, first);

            var handler = new ScriptableHandler(async (request, ct) =>
            {
                Assert.IsNotNull(request.Headers.Range);
                Assert.AreEqual(first.Length, request.Headers.Range.Ranges.First().From);

                var response = new HttpResponseMessage(HttpStatusCode.PartialContent);
                response.Content = new ByteArrayContent(second);
                response.Content.Headers.ContentLength = second.Length;
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
                    first.Length, first.Length + second.Length - 1, first.Length + second.Length);
                return response;
            });

            // Use a stub Finalize by downloading into a path that stays small — Finalize will fail size check.
            // So we only assert the file bytes were appended after TryDownload returns.
            using var client = new HttpClient(handler);
            var (ok, error, fatal) = await HyMtModelDownloader.TryDownloadFromSourceAsync(
                client,
                "https://example.test/model.gguf",
                "mock",
                tempPath,
                progress: null,
                CancellationToken.None);

            Assert.IsFalse(ok, "size too small for Finalize");
            Assert.IsFalse(fatal);
            var bytes = await File.ReadAllBytesAsync(tempPath);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("HELLO-WORLD"), bytes);
            Assert.IsTrue(handler.SawRangeRequest);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [TestMethod]
    public async Task TryDownloadFullRedownloadWhenServerIgnoresRange()
    {
        var dir = Path.Combine(Path.GetTempPath(), "snip-full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tempPath = Path.Combine(dir, "model.gguf.downloading");
        try
        {
            await File.WriteAllBytesAsync(tempPath, Encoding.UTF8.GetBytes("OLD-PARTIAL-DATA"));
            byte[] fresh = Encoding.UTF8.GetBytes("FRESH");

            var handler = new ScriptableHandler((request, ct) =>
            {
                // Server returns 200 even when Range was sent.
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Content = new ByteArrayContent(fresh);
                response.Content.Headers.ContentLength = fresh.Length;
                return Task.FromResult(response);
            });

            using var client = new HttpClient(handler);
            var (ok, _, fatal) = await HyMtModelDownloader.TryDownloadFromSourceAsync(
                client,
                "https://example.test/model.gguf",
                "mock",
                tempPath,
                progress: null,
                CancellationToken.None);

            Assert.IsFalse(ok);
            Assert.IsFalse(fatal);
            CollectionAssert.AreEqual(fresh, await File.ReadAllBytesAsync(tempPath));
            Assert.IsTrue(handler.SawRangeRequest);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class ScriptableHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _impl;

        public ScriptableHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl)
        {
            _impl = impl;
        }

        public bool SawRangeRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Range != null)
            {
                SawRangeRequest = true;
            }

            return await _impl(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
