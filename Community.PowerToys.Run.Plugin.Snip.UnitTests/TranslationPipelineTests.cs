// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Community.PowerToys.Run.Plugin.Snip;
using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class TranslationPipelineTests
{
    private sealed class FakeTranslator : ITranslator
    {
        private readonly Func<string, string> _map;

        public FakeTranslator(Func<string, string> map = null)
        {
            _map = map ?? (t => "T:" + t);
        }

        public List<string[]> Calls { get; } = [];

        public int TranslatedCount => Calls.Sum(c => c.Length);

        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts,
            string targetLanguage,
            IProgress<TranslatorProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(texts.ToArray());
            var result = texts.Select(_map).ToList();
            progress?.Report(new TranslatorProgress(texts.Count, texts.Count));
            return Task.FromResult<IReadOnlyList<string>>(result);
        }
    }

    private sealed class RecordingProgress : IProgress<TranslatorProgress>
    {
        public List<TranslatorProgress> Reports { get; } = [];

        public void Report(TranslatorProgress value) => Reports.Add(value);
    }

    private string _path;

    [TestInitialize]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), "snip-tp-" + Guid.NewGuid().ToString("N") + ".json");
        TranslationMemory.SetPathForTests(_path);
        TranslationMemory.Clear();
    }

    [TestCleanup]
    public void Cleanup()
    {
        TranslationMemory.Clear();
        TranslationMemory.SetPathForTests(null);
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static SnipSettings Settings(bool cache)
        => new()
        {
            TranslatorProvider = TranslatorProvider.HttpOpenAi,
            HttpModel = "test-model",
            TargetLanguage = "简体中文",
            TranslationCacheEnabled = cache,
        };

    [TestMethod]
    public async Task Translate_DeduplicatesIdenticalLinesAndPreservesOrder()
    {
        var translator = new FakeTranslator();

        var result = await TranslationPipeline.TranslateAsync(
            translator,
            Settings(cache: false),
            ["alpha", "beta", "alpha", " ", "beta", "gamma"]);

        Assert.HasCount(1, translator.Calls);
        CollectionAssert.AreEqual(new[] { "alpha", "beta", "gamma" }, translator.Calls[0]);
        CollectionAssert.AreEqual(
            new[] { "T:alpha", "T:beta", "T:alpha", string.Empty, "T:beta", "T:gamma" },
            result.ToArray());
    }

    [TestMethod]
    public async Task Translate_SecondRunIsServedFromCache()
    {
        var translator = new FakeTranslator();
        var settings = Settings(cache: true);
        string[] sources = ["hello", "world", "hello"];

        var first = await TranslationPipeline.TranslateAsync(translator, settings, sources);
        int callsAfterFirst = translator.TranslatedCount;
        Assert.AreEqual(2, callsAfterFirst);

        var second = await TranslationPipeline.TranslateAsync(translator, settings, sources);

        Assert.AreEqual(callsAfterFirst, translator.TranslatedCount, "cache hit must not call the translator");
        CollectionAssert.AreEqual(first.ToArray(), second.ToArray());
    }

    [TestMethod]
    public async Task Translate_ReportsCacheHitsPreroundedOnce()
    {
        var settings = Settings(cache: true);
        var translator = new FakeTranslator();
        TranslationMemory.Store(
            ["cached"],
            ["缓存"],
            TranslationMemory.BuildProviderKey(settings),
            settings.TargetLanguage);

        var progress = new RecordingProgress();
        var result = await TranslationPipeline.TranslateAsync(
            translator,
            settings,
            ["cached", "new"],
            progress);

        Assert.AreEqual("缓存", result[0]);
        Assert.AreEqual("T:new", result[1]);
        Assert.HasCount(1, translator.Calls);
        CollectionAssert.AreEqual(new[] { "new" }, translator.Calls[0]);

        // First report carries the cache hit; the last report reaches 2/2.
        Assert.IsTrue(progress.Reports.Count >= 2);
        Assert.AreEqual(1, progress.Reports[0].Completed);
        Assert.AreEqual(2, progress.Reports[0].Total);
        Assert.AreEqual(2, progress.Reports[^1].Completed);
        Assert.AreEqual(2, progress.Reports[^1].Total);
    }

    [TestMethod]
    public async Task Translate_DoesNotCacheFallbacksEqualToSource()
    {
        var translator = new FakeTranslator(t => t);
        var settings = Settings(cache: true);

        var result = await TranslationPipeline.TranslateAsync(translator, settings, ["same"]);

        Assert.AreEqual("same", result[0]);
        Assert.AreEqual(0, TranslationMemory.Count);
    }

    [TestMethod]
    public async Task Translate_AllWhitespaceNeedsNoTranslatorCall()
    {
        var translator = new FakeTranslator();

        var result = await TranslationPipeline.TranslateAsync(
            translator,
            Settings(cache: true),
            ["", "   ", null]);

        Assert.HasCount(0, translator.Calls);
        CollectionAssert.AreEqual(new[] { string.Empty, string.Empty, string.Empty }, result.ToArray());
    }
}
