// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class TranslatorProgressTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<int, string> _replyByCall;

        public RecordingHandler(Func<int, string> replyByCall)
        {
            _replyByCall = replyByCall;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int n = ++Calls;
            string content = _replyByCall(n);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class SyncProgress : IProgress<TranslatorProgress>
    {
        public List<TranslatorProgress> Ticks { get; } = new();

        public void Report(TranslatorProgress value)
        {
            lock (Ticks)
            {
                Ticks.Add(value);
            }
        }
    }

    [TestMethod]
    public async Task HttpTranslator_ReportsProgressPerChunkAndLine()
    {
        // Chunk parse fails on call 1 (malformed), so it falls back to per-line
        // calls 2 and 3 — progress should tick 1 of 2, then 2 of 2.
        var handler = new RecordingHandler(n => n switch
        {
            1 => """{"choices":[{"message":{"content":"garbage"}}]}""",
            _ => """{"choices":[{"message":{"content":"1. 你好"}}]}""",
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:9/v1/"),
        };
        using var translator = new HttpOpenAiTranslator("http://127.0.0.1:9/v1", apiKey: null, model: "test", http);

        var progress = new SyncProgress();
        var results = await translator.TranslateAsync(new[] { "Hello", "World" }, "简体中文", progress);

        // Fallback path echoes per-line replies verbatim ("1. 你好" keeps its prefix).
        CollectionAssert.AreEqual(new[] { "1. 你好", "1. 你好" }, results.ToArray());
        Assert.AreEqual(3, handler.Calls, "one batch attempt + two per-line fallbacks");

        var ticks = progress.Ticks.ToList();
        Assert.AreEqual(2, ticks.Count, string.Join(", ", ticks));
        Assert.AreEqual(1, ticks[0].Completed);
        Assert.AreEqual(2, ticks[0].Total);
        Assert.AreEqual(2, ticks[1].Completed);
        Assert.AreEqual(2, ticks[1].Total);
    }

    [TestMethod]
    public async Task NullProgressIsAccepted()
    {
        using var http = new HttpClient(new RecordingHandler(_ => """{"choices":[{"message":{"content":"1. 你好\n2. 世界"}}]}"""))
        {
            BaseAddress = new Uri("http://127.0.0.1:9/v1/"),
        };
        using var translator = new HttpOpenAiTranslator("http://127.0.0.1:9/v1", apiKey: null, model: "test", http);

        var results = await translator.TranslateAsync(new[] { "Hello", "World" }, "简体中文", progress: null);
        Assert.AreEqual(2, results.Count);
        Assert.IsTrue(results.All(r => r.Length > 0));
    }
}
