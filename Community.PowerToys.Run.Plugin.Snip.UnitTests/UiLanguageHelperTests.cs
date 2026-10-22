// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Resources = Community.PowerToys.Run.Plugin.Snip.Properties.Resources;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class UiLanguageHelperTests
{
    [TestMethod]
    public void ResolveCultureNameMapsSystemEnZhCN()
    {
        Assert.IsNull(UiLanguageHelper.ResolveCultureName(null));
        Assert.IsNull(UiLanguageHelper.ResolveCultureName(string.Empty));
        Assert.IsNull(UiLanguageHelper.ResolveCultureName("system"));
        Assert.IsNull(UiLanguageHelper.ResolveCultureName("SYSTEM"));
        Assert.IsNull(UiLanguageHelper.ResolveCultureName("auto"));

        Assert.AreEqual("en", UiLanguageHelper.ResolveCultureName("en"));
        Assert.AreEqual("en", UiLanguageHelper.ResolveCultureName("en-US"));
        Assert.AreEqual("zh-CN", UiLanguageHelper.ResolveCultureName("zh-CN"));
        Assert.AreEqual("zh-CN", UiLanguageHelper.ResolveCultureName("zh-Hans"));
    }

    [TestMethod]
    public void NormalizeAndPersistedValue()
    {
        Assert.AreEqual(UiLanguageHelper.System, UiLanguageHelper.Normalize(""));
        Assert.AreEqual(UiLanguageHelper.English, UiLanguageHelper.Normalize("en-GB"));
        Assert.AreEqual(string.Empty, UiLanguageHelper.ToPersistedValue("system"));
        Assert.AreEqual("en", UiLanguageHelper.ToPersistedValue("en"));
        Assert.AreEqual("zh-CN", UiLanguageHelper.ToPersistedValue("zh"));
    }

    [TestMethod]
    public void ResolveCultureMatchesName()
    {
        Assert.IsNull(UiLanguageHelper.ResolveCulture("system"));
        Assert.AreEqual("en", UiLanguageHelper.ResolveCulture("en").Name);
        Assert.AreEqual("zh-CN", UiLanguageHelper.ResolveCulture("zh-CN").Name);
    }

    [TestMethod]
    public void ApplySetsResourcesCulture()
    {
        try
        {
            UiLanguageHelper.Apply("en");
            Assert.IsNotNull(Resources.Culture);
            Assert.AreEqual("en", Resources.Culture.Name);

            UiLanguageHelper.Apply("zh-CN");
            Assert.AreEqual("zh-CN", Resources.Culture.Name);

            UiLanguageHelper.Apply("system");
            Assert.IsNull(Resources.Culture);
        }
        finally
        {
            UiLanguageHelper.Apply("system");
        }
    }
}
