// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Community.PowerToys.Run.Plugin.Snip.Models;
using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class SnipSettingsPersisterTests
{
    [TestMethod]
    public void TryPatchWritesMultipleOptionKinds()
    {
        var path = Path.Combine(Path.GetTempPath(), "snip-pt-settings-multi-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var initial = """
                {
                  "name": "PowerToys Run",
                  "plugins": [
                    {
                      "Id": "7667E8C5E74948F7B2D6B5E7CED60783",
                      "Name": "Snip",
                      "AdditionalOptions": [
                        { "Key": "SaveDirectory", "TextValue": "" },
                        { "Key": "CopyAfterCapture", "Value": true },
                        { "Key": "CaptureDelayMs", "NumberValue": 150 },
                        { "Key": "TranslatorProvider", "ComboBoxValue": 0 },
                        { "Key": "GlobalHotkey", "TextValue": "" }
                      ]
                    }
                  ]
                }
                """;
            File.WriteAllText(path, initial);

            Assert.IsTrue(SnipSettingsPersister.TryPatchPowerToysRunSettings(
                [
                    SnipSettingsPersister.OptionPatch.Text("SaveDirectory", @"D:\shots"),
                    SnipSettingsPersister.OptionPatch.Bool("CopyAfterCapture", false),
                    SnipSettingsPersister.OptionPatch.Number("CaptureDelayMs", 300),
                    SnipSettingsPersister.OptionPatch.ComboBox("TranslatorProvider", 1),
                    SnipSettingsPersister.OptionPatch.Text("GlobalHotkey", "Ctrl+Shift+S"),
                ],
                path));

            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var options = root["plugins"]![0]!["AdditionalOptions"]!.AsArray();
            Assert.AreEqual(@"D:\shots", Find(options, "SaveDirectory")!["TextValue"]!.GetValue<string>());
            Assert.IsFalse(Find(options, "CopyAfterCapture")!["Value"]!.GetValue<bool>());
            Assert.AreEqual(300, Find(options, "CaptureDelayMs")!["NumberValue"]!.GetValue<double>());
            Assert.AreEqual(1, Find(options, "TranslatorProvider")!["ComboBoxValue"]!.GetValue<int>());
            Assert.AreEqual("Ctrl+Shift+S", Find(options, "GlobalHotkey")!["TextValue"]!.GetValue<string>());
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
    public void BuildPatchesFromSettingsIncludesHotkeyAndTranslator()
    {
        var settings = new SnipSettings
        {
            SaveDirectory = @"C:\x",
            GlobalHotkey = "Alt+Shift+S",
            TranslatorProvider = TranslatorProvider.HttpOpenAi,
            CaptureDelayMs = 200,
            CopyAfterCapture = false,
        };

        var patches = SnipSettingsPersister.BuildPatchesFromSettings(settings);
        Assert.IsTrue(patches.Any(p => p.Key == "GlobalHotkey" && p.TextValue == "Alt+Shift+S"));
        Assert.IsTrue(patches.Any(p => p.Key == "TranslatorProvider" && p.ComboBoxValue == 1));
        Assert.IsTrue(patches.Any(p => p.Key == "CopyAfterCapture" && p.BoolValue == false));
        Assert.IsTrue(patches.Any(p => p.Key == "CaptureDelayMs" && p.NumberValue == 200));
    }

    private static JsonNode Find(JsonArray options, string key)
        => options.First(o => o!["Key"]!.GetValue<string>() == key)!;
}
