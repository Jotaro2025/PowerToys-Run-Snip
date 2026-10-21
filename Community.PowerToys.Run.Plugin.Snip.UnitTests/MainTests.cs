// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Community.PowerToys.Run.Plugin.Snip;
using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Wox.Plugin;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class MainTests
{
    private CultureInfo originalCulture;
    private CultureInfo originalUiCulture;

    [TestInitialize]
    public void Setup()
    {
        // Resource lookups follow CurrentUICulture; pin it so the assertions
        // see the same (English) strings on any machine locale.
        originalCulture = CultureInfo.CurrentCulture;
        originalUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-us", false);
        CultureInfo.CurrentUICulture = new CultureInfo("en-us", false);
    }

    [TestCleanup]
    public void Cleanup()
    {
        CultureInfo.CurrentCulture = originalCulture;
        CultureInfo.CurrentUICulture = originalUiCulture;
    }

    private static Main CreateMain()
    {
        var api = new Mock<IPublicAPI>();
        api.Setup(a => a.GetCurrentTheme()).Returns(Theme.Dark);

        var main = new Main();
        main.Init(new PluginInitContext { API = api.Object });
        return main;
    }

    [TestMethod]
    public void QueryWithoutSearchReturnsAllCommands()
    {
        var main = CreateMain();

        var results = main.Query(new Query(string.Empty));

        Assert.HasCount(3, results);
        Assert.IsTrue(results.All(r => r.Action != null));
    }

    [TestMethod]
    public void QueryFiltersByTitle()
    {
        var main = CreateMain();

        var results = main.Query(new Query("clipboard"));

        Assert.HasCount(1, results);
        Assert.AreEqual(Properties.Resources.cmd_full_clipboard_title, results[0].Title);
    }

    [TestMethod]
    public void QueryReturnsEmptyForUnknownSearch()
    {
        var main = CreateMain();

        var results = main.Query(new Query("nonexistent-command"));

        Assert.HasCount(0, results);
    }

    [TestMethod]
    public void UpdateSettingsReadsAllOptions()
    {
        var main = CreateMain();
        var settings = new PowerLauncherPluginSettings
        {
            AdditionalOptions = new List<PluginAdditionalOption>
            {
                new PluginAdditionalOption { Key = "SaveDirectory", TextValue = @"D:\shots" },
                new PluginAdditionalOption { Key = "CopyAfterCapture", Value = false },
                new PluginAdditionalOption { Key = "AutoSaveAfterCapture", Value = true },
                new PluginAdditionalOption { Key = "CaptureDelayMs", NumberValue = 300 },
                new PluginAdditionalOption { Key = "WeChatDirectory", TextValue = @"D:\WeChat" },
            },
        };

        main.UpdateSettings(settings);

        var current = main.AdditionalOptions.ToDictionary(x => x.Key);
        Assert.AreEqual(@"D:\shots", current["SaveDirectory"].TextValue);
        Assert.IsFalse(current["CopyAfterCapture"].Value);
        Assert.IsTrue(current["AutoSaveAfterCapture"].Value);
        Assert.AreEqual(300, current["CaptureDelayMs"].NumberValue);
        Assert.AreEqual(@"D:\WeChat", current["WeChatDirectory"].TextValue);
    }

    [TestMethod]
    public void UpdateSettingsKeepsDefaultsForMissingOptions()
    {
        var main = CreateMain();

        main.UpdateSettings(new PowerLauncherPluginSettings { AdditionalOptions = new List<PluginAdditionalOption>() });

        var current = main.AdditionalOptions.ToDictionary(x => x.Key);
        Assert.IsTrue(current["CopyAfterCapture"].Value, "copy after capture is on by default");
        Assert.IsFalse(current["AutoSaveAfterCapture"].Value);
        Assert.AreEqual(150, current["CaptureDelayMs"].NumberValue);
        Assert.AreEqual(string.Empty, current["WeChatDirectory"].TextValue, "WeChat folder is detected automatically by default");
    }

    [TestMethod]
    public void UpdateSettingsToleratesNullSettings()
    {
        var main = CreateMain();

        main.UpdateSettings(null);

        Assert.HasCount(5, main.AdditionalOptions.ToList());
    }

    [TestMethod]
    public void TranslatedTitleAndDescriptionComeFromResources()
    {
        var main = CreateMain();

        Assert.AreEqual(Properties.Resources.plugin_name, main.GetTranslatedPluginTitle());
        Assert.AreEqual(Properties.Resources.plugin_description, main.GetTranslatedPluginDescription());
    }
}
