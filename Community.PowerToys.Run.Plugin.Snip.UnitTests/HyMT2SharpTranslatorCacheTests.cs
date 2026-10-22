// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading.Tasks;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class HyMT2SharpTranslatorCacheTests
{
    [TestCleanup]
    public void Cleanup()
    {
        HyMT2SharpTranslatorCache.Unload();
        HyMT2SharpTranslatorCache.ConfigureIdleUnload(HyMT2SharpTranslatorCache.DefaultIdleUnloadMinutes);
    }

    [TestMethod]
    public void TryUnloadIfIdle_UnloadsAfterTimeout()
    {
        var path = MakeTempPath("idle");
        HyMT2SharpTranslatorCache.ConfigureIdleUnload(5);
        HyMT2SharpTranslatorCache.GetOrCreate(path, HyMtBackendKind.Cpu);

        Assert.IsFalse(HyMT2SharpTranslatorCache.TryUnloadIfIdle(DateTime.UtcNow.AddMinutes(1)));
        Assert.IsTrue(HyMT2SharpTranslatorCache.IsCached(path, HyMtBackendKind.Cpu));

        Assert.IsTrue(HyMT2SharpTranslatorCache.TryUnloadIfIdle(DateTime.UtcNow.AddMinutes(6)));
        Assert.IsFalse(HyMT2SharpTranslatorCache.IsCached(path, HyMtBackendKind.Cpu));
    }

    [TestMethod]
    public void TryUnloadIfIdle_DisabledWhenZero()
    {
        var path = MakeTempPath("idle-off");
        HyMT2SharpTranslatorCache.ConfigureIdleUnload(0);
        HyMT2SharpTranslatorCache.GetOrCreate(path, HyMtBackendKind.Cpu);

        Assert.AreEqual(TimeSpan.Zero, HyMT2SharpTranslatorCache.IdleTimeout);
        Assert.IsFalse(HyMT2SharpTranslatorCache.TryUnloadIfIdle(DateTime.UtcNow.AddDays(1)));
        Assert.IsTrue(HyMT2SharpTranslatorCache.IsCached(path, HyMtBackendKind.Cpu));
    }

    [TestMethod]
    public void ConfigureIdleUnload_ClampsMinutes()
    {
        HyMT2SharpTranslatorCache.ConfigureIdleUnload(10_000);
        Assert.AreEqual(TimeSpan.FromMinutes(HyMT2SharpTranslatorCache.MaxIdleUnloadMinutes), HyMT2SharpTranslatorCache.IdleTimeout);
        HyMT2SharpTranslatorCache.ConfigureIdleUnload(-3);
        Assert.AreEqual(TimeSpan.Zero, HyMT2SharpTranslatorCache.IdleTimeout);
    }

    [TestMethod]
    public void GetOrCreateReturnsSameInstanceForSameKey()
    {
        var path = MakeTempPath("same");
        var a = HyMT2SharpTranslatorCache.GetOrCreate(path, HyMtBackendKind.Cpu);
        var b = HyMT2SharpTranslatorCache.GetOrCreate(path, HyMtBackendKind.Cpu);

        Assert.AreSame(a, b);
        Assert.IsTrue(HyMT2SharpTranslatorCache.IsCached(path, HyMtBackendKind.Cpu));
    }

    [TestMethod]
    public void GetOrCreateNormalizesRelativePathKeys()
    {
        var absolute = Path.GetFullPath(MakeTempPath("norm"));
        var relativeDir = Path.GetDirectoryName(absolute);
        var fileName = Path.GetFileName(absolute);
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(relativeDir);
            var viaRelative = HyMT2SharpTranslatorCache.GetOrCreate(fileName, HyMtBackendKind.Auto);
            var viaAbsolute = HyMT2SharpTranslatorCache.GetOrCreate(absolute, HyMtBackendKind.Auto);
            Assert.AreSame(viaRelative, viaAbsolute);
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }

    [TestMethod]
    public async Task GetOrCreateReplacesAndDisposesWhenBackendChanges()
    {
        var path = MakeTempPath("backend");
        var first = HyMT2SharpTranslatorCache.GetOrCreate(path, HyMtBackendKind.Cpu);
        var second = HyMT2SharpTranslatorCache.GetOrCreate(path, HyMtBackendKind.Vulkan);

        Assert.AreNotSame(first, second);
        Assert.IsTrue(HyMT2SharpTranslatorCache.IsCached(path, HyMtBackendKind.Vulkan));
        Assert.IsFalse(HyMT2SharpTranslatorCache.IsCached(path, HyMtBackendKind.Cpu));

        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(
            () => first.TranslateAsync(new[] { "hi" }, "English"));
    }

    [TestMethod]
    public void GetOrCreateReplacesWhenPathChanges()
    {
        var pathA = MakeTempPath("a");
        var pathB = MakeTempPath("b");
        var first = HyMT2SharpTranslatorCache.GetOrCreate(pathA, HyMtBackendKind.Auto);
        var second = HyMT2SharpTranslatorCache.GetOrCreate(pathB, HyMtBackendKind.Auto);

        Assert.AreNotSame(first, second);
        Assert.IsTrue(HyMT2SharpTranslatorCache.IsCached(pathB, HyMtBackendKind.Auto));
        Assert.IsFalse(HyMT2SharpTranslatorCache.IsCached(pathA, HyMtBackendKind.Auto));
    }

    [TestMethod]
    public async Task UnloadClearsCacheAndDisposesInstance()
    {
        var path = MakeTempPath("unload");
        var instance = HyMT2SharpTranslatorCache.GetOrCreate(path, HyMtBackendKind.Cpu);
        HyMT2SharpTranslatorCache.Unload();

        Assert.IsFalse(HyMT2SharpTranslatorCache.IsCached(path, HyMtBackendKind.Cpu));

        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(
            () => instance.TranslateAsync(new[] { "hi" }, "English"));
    }

    [TestMethod]
    public void UnloadIsIdempotentWhenEmpty()
    {
        HyMT2SharpTranslatorCache.Unload();
        HyMT2SharpTranslatorCache.Unload();
        Assert.IsFalse(HyMT2SharpTranslatorCache.IsCached(MakeTempPath("empty"), HyMtBackendKind.Auto));
    }

    private static string MakeTempPath(string tag)
    {
        return Path.Combine(Path.GetTempPath(), $"snip-hymt-cache-{tag}-{Path.GetRandomFileName()}.gguf");
    }
}
