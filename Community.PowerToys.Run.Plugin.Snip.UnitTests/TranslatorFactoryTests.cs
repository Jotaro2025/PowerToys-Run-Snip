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
public class TranslatorFactoryTests
{
    [TestMethod]
    public void ValidateReportsOffProvider()
    {
        var error = TranslatorFactory.Validate(new SnipSettings { TranslatorProvider = TranslatorProvider.Off });
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void ValidateAcceptsHttpWhenConfigured()
    {
        var error = TranslatorFactory.Validate(new SnipSettings
        {
            TranslatorProvider = TranslatorProvider.HttpOpenAi,
            HttpBaseUrl = "http://127.0.0.1:8080/v1",
            HttpModel = "hy-mt2",
            TargetLanguage = "English",
        });
        Assert.IsNull(error);
    }

    [TestMethod]
    public void ValidateRejectsHttpWithoutUrl()
    {
        var error = TranslatorFactory.Validate(new SnipSettings
        {
            TranslatorProvider = TranslatorProvider.HttpOpenAi,
            HttpBaseUrl = string.Empty,
            HttpModel = "hy-mt2",
            TargetLanguage = "English",
        });
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void ValidateRejectsHyMtWithoutExistingFile()
    {
        var error = TranslatorFactory.Validate(new SnipSettings
        {
            TranslatorProvider = TranslatorProvider.HyMT2Sharp,
            HyMtGgufPath = Path.Combine(Path.GetTempPath(), "snip-missing-" + Path.GetRandomFileName() + ".gguf"),
            TargetLanguage = "简体中文",
        });
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }
}
