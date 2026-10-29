// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Community.PowerToys.Run.Plugin.Snip;
using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class TencentTranslateTranslatorTests
{
    // Vector computed independently with a reference TC3-HMAC-SHA256 implementation.
    private const string ReferenceSecretId = "AKIDEXAMPLE1234567890";
    private const string ReferenceSecretKey = "SECRETKEYEXAMPLE1234567890";
    private const long ReferenceTimestamp = 1700000000;
    private const string ReferencePayload = "{\"SourceText\":\"Hello world\",\"Source\":\"auto\",\"Target\":\"zh\",\"ProjectId\":0}";
    private const string ReferenceAuthorization =
        "TC3-HMAC-SHA256 Credential=AKIDEXAMPLE1234567890/2023-11-14/tmt/tc3_request, "
        + "SignedHeaders=content-type;host, Signature=5529c70a6c7f3c21b617f72ff3c103e7bcff81345ed608d76176477a91440cb5";

    [TestMethod]
    public void BuildPayloadUsesTextTranslateShape()
    {
        Assert.AreEqual(ReferencePayload, TencentTranslateTranslator.BuildPayload("Hello world", "zh"));
    }

    [TestMethod]
    public void BuildAuthorizationMatchesReferenceVector()
    {
        string authorization = TencentTranslateTranslator.BuildAuthorization(
            ReferenceSecretId,
            ReferenceSecretKey,
            ReferencePayload,
            ReferenceTimestamp);

        Assert.AreEqual(ReferenceAuthorization, authorization);
    }

    [TestMethod]
    public void BuildAuthorizationChangesWithPayload()
    {
        string a = TencentTranslateTranslator.BuildAuthorization(ReferenceSecretId, ReferenceSecretKey, ReferencePayload, ReferenceTimestamp);
        string b = TencentTranslateTranslator.BuildAuthorization(
            ReferenceSecretId,
            ReferenceSecretKey,
            TencentTranslateTranslator.BuildPayload("Hello world!", "zh"),
            ReferenceTimestamp);

        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public void ResolvesLanguageCodes()
    {
        Assert.AreEqual("zh", TencentTranslateTranslator.ResolveLanguageCode("简体中文"));
        Assert.AreEqual("zh", TencentTranslateTranslator.ResolveLanguageCode("zh"));
        Assert.AreEqual("zh-TW", TencentTranslateTranslator.ResolveLanguageCode("繁體中文"));
        Assert.AreEqual("zh-TW", TencentTranslateTranslator.ResolveLanguageCode("ZH-tw"));
        Assert.AreEqual("en", TencentTranslateTranslator.ResolveLanguageCode("English"));
        Assert.AreEqual("ja", TencentTranslateTranslator.ResolveLanguageCode("日语"));
        Assert.AreEqual("ko", TencentTranslateTranslator.ResolveLanguageCode("한국어"));
        Assert.AreEqual("de", TencentTranslateTranslator.ResolveLanguageCode("德语"));
        Assert.ThrowsException<TranslatorException>(() => TencentTranslateTranslator.ResolveLanguageCode("Klingon"));
        Assert.ThrowsException<TranslatorException>(() => TencentTranslateTranslator.ResolveLanguageCode(" "));
    }

    [TestMethod]
    public async Task TranslateAsyncBatchesLinesAndSplitsReply()
    {
        var handler = new QueueHandler(
            """{"Response":{"TargetText":"一\n二\n三","RequestId":"r"}}""");

        using var http = new HttpClient(handler);
        using var translator = new TencentTranslateTranslator("id", "key", "ap-guangzhou", http);

        var results = await translator.TranslateAsync(new[] { "one", "two", "three" }, "简体中文");

        CollectionAssert.AreEqual(new[] { "一", "二", "三" }, new List<string>(results));
        Assert.AreEqual(1, handler.Requests.Count);
        StringAssert.Contains(handler.Requests[0].Body, "\"SourceText\":\"one\\ntwo\\nthree\"");
        Assert.AreEqual("TextTranslate", handler.Requests[0].Action);
        Assert.AreEqual("ap-guangzhou", handler.Requests[0].Region);
    }

    [TestMethod]
    public async Task TranslateAsyncFallsBackWhenLineCountDiffers()
    {
        var handler = new QueueHandler(
            """{"Response":{"TargetText":"merged","RequestId":"r"}}""",
            """{"Response":{"TargetText":"一","RequestId":"r"}}""",
            """{"Response":{"TargetText":"二","RequestId":"r"}}""");

        using var http = new HttpClient(handler);
        using var translator = new TencentTranslateTranslator("id", "key", "ap-guangzhou", http);

        var results = await translator.TranslateAsync(new[] { "one", "two" }, "en");

        CollectionAssert.AreEqual(new[] { "一", "二" }, new List<string>(results));
        Assert.AreEqual(3, handler.Requests.Count);
        Assert.AreEqual(0, translator.LastPartialFallbackCount);
    }

    [TestMethod]
    public async Task TranslateAsyncSurfacesApiError()
    {
        var handler = new QueueHandler(
            """{"Response":{"Error":{"Code":"AuthFailure.SignatureFailure","Message":"bad signature"},"RequestId":"r"}}""");

        using var http = new HttpClient(handler);
        using var translator = new TencentTranslateTranslator("id", "key", "ap-guangzhou", http);

        var ex = await Assert.ThrowsExceptionAsync<TranslatorException>(
            () => translator.TranslateAsync(new[] { "one" }, "en"));
        StringAssert.Contains(ex.Message, "AuthFailure.SignatureFailure");
    }

    [TestMethod]
    public async Task TranslateAsyncKeepsOriginalTextWhenEveryLineFails()
    {
        var handler = new QueueHandler(
            """{"Response":{"Error":{"Code":"RequestLimitExceeded","Message":"slow down"},"RequestId":"r"}}""",
            """{"Response":{"Error":{"Code":"RequestLimitExceeded","Message":"slow down"},"RequestId":"r"}}""",
            """{"Response":{"Error":{"Code":"RequestLimitExceeded","Message":"slow down"},"RequestId":"r"}}""");

        using var http = new HttpClient(handler);
        using var translator = new TencentTranslateTranslator("id", "key", "ap-guangzhou", http);

        await Assert.ThrowsExceptionAsync<TranslatorException>(
            () => translator.TranslateAsync(new[] { "one", "two" }, "en"));
    }

    [TestMethod]
    public void FactoryValidatesTencentSettings()
    {
        var settings = new SnipSettings
        {
            TranslatorProvider = TranslatorProvider.Tencent,
            TargetLanguage = "简体中文",
        };

        Assert.AreEqual(
            Community.PowerToys.Run.Plugin.Snip.Properties.Resources.tr_err_tencent_key,
            TranslatorFactory.Validate(settings));

        settings.TencentSecretId = "id";
        settings.TencentSecretKey = "key";
        Assert.IsNull(TranslatorFactory.Validate(settings));

        settings.TargetLanguage = "Klingon";
        Assert.IsNotNull(TranslatorFactory.Validate(settings));
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public QueueHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public List<(string Body, string Action, string Region, string Authorization)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Requests.Add((
                body,
                Header(request, "X-TC-Action"),
                Header(request, "X-TC-Region"),
                Header(request, "Authorization")));

            string next = _responses.Count > 0 ? _responses.Dequeue() : "{\"Response\":{}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(next, Encoding.UTF8, "application/json"),
            };
        }

        private static string Header(HttpRequestMessage request, string name)
        {
            if (request.Headers.TryGetValues(name, out var values))
            {
                foreach (string v in values)
                {
                    return v;
                }
            }

            return string.Empty;
        }
    }
}
