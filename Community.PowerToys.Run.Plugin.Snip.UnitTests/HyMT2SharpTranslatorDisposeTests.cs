// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class HyMT2SharpTranslatorDisposeTests
{
    private TimeSpan _savedTimeout;

    [TestInitialize]
    public void Init() => _savedTimeout = HyMT2SharpTranslator.DisposeWaitTimeout;

    [TestCleanup]
    public void Cleanup() => HyMT2SharpTranslator.DisposeWaitTimeout = _savedTimeout;

    [TestMethod]
    public void Dispose_IsIdempotent()
    {
        var t = new HyMT2SharpTranslator(TempGguf(), HyMtBackendKind.Cpu);
        t.Dispose();
        t.Dispose();
    }

    [TestMethod]
    public async Task TranslateAfterDispose_Throws()
    {
        var t = new HyMT2SharpTranslator(TempGguf(), HyMtBackendKind.Cpu);
        t.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => t.TranslateAsync(new[] { "hi" }, "English"));
    }

    [TestMethod]
    public void Dispose_WhileBusy_ReturnsAfterTimeout()
    {
        HyMT2SharpTranslator.DisposeWaitTimeout = TimeSpan.FromMilliseconds(100);
        var t = new HyMT2SharpTranslator(TempGguf(), HyMtBackendKind.Cpu);

        // Simulate an in-flight translation that never yields the gate.
        t.GateForTests.Wait();
        Assert.IsTrue(t.IsBusy);

        var sw = Stopwatch.StartNew();
        t.Dispose();
        sw.Stop();

        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(3), $"Dispose blocked {sw.Elapsed}");

        // The "in-flight" call finishing later must not throw.
        t.GateForTests.Release();
    }

    private static string TempGguf()
        => Path.Combine(Path.GetTempPath(), $"snip-hymt-dispose-{Path.GetRandomFileName()}.gguf");
}
