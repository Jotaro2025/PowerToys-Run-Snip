// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Community.PowerToys.Run.Plugin.Snip;
using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class TranslationMemoryTests
{
    private string _path;
    private SnipSettings _settings;

    [TestInitialize]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), "snip-tm-" + Guid.NewGuid().ToString("N") + ".json");
        TranslationMemory.SetPathForTests(_path);
        TranslationMemory.Clear();

        _settings = new SnipSettings
        {
            TranslatorProvider = TranslatorProvider.HttpOpenAi,
            HttpModel = "test-model",
            TargetLanguage = "简体中文",
        };
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

    [TestMethod]
    public void StoreAndLookup_RoundTripsExactPairs()
    {
        string key = TranslationMemory.BuildProviderKey(_settings);

        TranslationMemory.Store(["hello", "world"], ["你好", "世界"], key, "简体中文");

        var found = TranslationMemory.Lookup(["hello", "world", "missing"], key, "简体中文");
        Assert.AreEqual("你好", found[0]);
        Assert.AreEqual("世界", found[1]);
        Assert.IsNull(found[2]);
        Assert.AreEqual(2, TranslationMemory.Count);
    }

    [TestMethod]
    public void Lookup_IsIsolatedByProviderModelAndTarget()
    {
        string key = TranslationMemory.BuildProviderKey(_settings);
        TranslationMemory.Store(["hello"], ["你好"], key, "简体中文");

        Assert.IsNull(TranslationMemory.Lookup(["hello"], key + "x", "简体中文")[0], "provider/model key must isolate");
        Assert.IsNull(TranslationMemory.Lookup(["hello"], key, "English")[0], "target language must isolate");

        var other = new SnipSettings { TranslatorProvider = TranslatorProvider.HttpOpenAi, HttpModel = "other" };
        Assert.IsNull(TranslationMemory.Lookup(["hello"], TranslationMemory.BuildProviderKey(other), "简体中文")[0]);
    }

    [TestMethod]
    public void Store_SkipsEmptyAndUntranslatedFallbacks()
    {
        string key = TranslationMemory.BuildProviderKey(_settings);

        TranslationMemory.Store(["same", "blank", "ok"], ["same", string.Empty, "好"], key, "简体中文");

        Assert.AreEqual(1, TranslationMemory.Count);
        Assert.IsNull(TranslationMemory.Lookup(["same"], key, "简体中文")[0]);
        Assert.IsNull(TranslationMemory.Lookup(["blank"], key, "简体中文")[0]);
        Assert.AreEqual("好", TranslationMemory.Lookup(["ok"], key, "简体中文")[0]);
    }

    [TestMethod]
    public void Flush_PersistsAcrossReload()
    {
        string key = TranslationMemory.BuildProviderKey(_settings);
        TranslationMemory.Store(["persisted"], ["已保存"], key, "简体中文");
        TranslationMemory.Flush();
        Assert.IsTrue(File.Exists(_path));

        // Redirect to the same file to drop the in-memory dictionary.
        TranslationMemory.SetPathForTests(_path);

        Assert.AreEqual("已保存", TranslationMemory.Lookup(["persisted"], key, "简体中文")[0]);
    }

    [TestMethod]
    public void Clear_RemovesEntriesAndFile()
    {
        string key = TranslationMemory.BuildProviderKey(_settings);
        TranslationMemory.Store(["x"], ["叉"], key, "简体中文");
        TranslationMemory.Flush();
        Assert.IsTrue(File.Exists(_path));

        TranslationMemory.Clear();

        Assert.AreEqual(0, TranslationMemory.Count);
        Assert.IsFalse(File.Exists(_path));
    }

    [TestMethod]
    public void Store_TrimsToMaxEntries()
    {
        string key = TranslationMemory.BuildProviderKey(_settings);
        var sources = Enumerable.Range(0, TranslationMemory.MaxEntries + 25)
            .Select(i => "source-" + i)
            .ToList();
        var values = sources.Select(s => "T:" + s).ToList();

        TranslationMemory.Store(sources, values, key, "简体中文");

        Assert.AreEqual(TranslationMemory.MaxEntries, TranslationMemory.Count);
        Assert.AreEqual("T:source-0", TranslationMemory.Lookup(["source-0"], key, "简体中文")[0]);
    }

    [TestMethod]
    public void BuildProviderKey_ChangesWithModelAndBackend()
    {
        var http = new SnipSettings { TranslatorProvider = TranslatorProvider.HttpOpenAi, HttpModel = "a" };
        var http2 = new SnipSettings { TranslatorProvider = TranslatorProvider.HttpOpenAi, HttpModel = "b" };
        Assert.AreNotEqual(TranslationMemory.BuildProviderKey(http), TranslationMemory.BuildProviderKey(http2));

        var hymt = new SnipSettings { TranslatorProvider = TranslatorProvider.HyMT2Sharp, HyMtGgufPath = @"C:\m\model.gguf" };
        var hymtVulkan = new SnipSettings
        {
            TranslatorProvider = TranslatorProvider.HyMT2Sharp,
            HyMtGgufPath = @"C:\m\model.gguf",
            HyMtBackend = HyMtBackendKind.Vulkan,
        };
        Assert.AreNotEqual(TranslationMemory.BuildProviderKey(hymt), TranslationMemory.BuildProviderKey(hymtVulkan));
        StringAssert.Contains(TranslationMemory.BuildProviderKey(hymt), "model.gguf");
    }
}
