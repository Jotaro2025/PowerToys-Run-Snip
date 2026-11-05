// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

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
            OcrBackend = OcrBackendKind.Vulkan,
            TranslationCacheEnabled = false,
            PrewarmOcrOnCapture = false,
            PrewarmTranslatorOnTranslate = false,
            LocalApiEnabled = true,
            LocalApiPort = 19999,
            LocalApiToken = "test-token",
            LocalApiAllowedOrigins = "chrome-extension://abc",
        };

        var patches = SnipSettingsPersister.BuildPatchesFromSettings(settings);
        Assert.IsTrue(patches.Any(p => p.Key == "GlobalHotkey" && p.TextValue == "Alt+Shift+S"));
        Assert.IsTrue(patches.Any(p => p.Key == "TranslatorProvider" && p.ComboBoxValue == 1));
        Assert.IsTrue(patches.Any(p => p.Key == "CopyAfterCapture" && p.BoolValue == false));
        Assert.IsTrue(patches.Any(p => p.Key == "CaptureDelayMs" && p.NumberValue == 200));
        Assert.IsTrue(patches.Any(p => p.Key == "OcrBackend" && p.ComboBoxValue == (int)OcrBackendKind.Vulkan));
        Assert.IsTrue(patches.Any(p => p.Key == "TranslationCacheEnabled" && p.BoolValue == false));
        Assert.IsTrue(patches.Any(p => p.Key == "PrewarmOcrOnCapture" && p.BoolValue == false));
        Assert.IsTrue(patches.Any(p => p.Key == "PrewarmTranslatorOnTranslate" && p.BoolValue == false));
        Assert.IsTrue(patches.Any(p => p.Key == "LocalApiEnabled" && p.BoolValue == true));
        Assert.IsTrue(patches.Any(p => p.Key == "LocalApiPort" && p.NumberValue == 19999));
        Assert.IsTrue(patches.Any(p => p.Key == "LocalApiToken" && !string.IsNullOrEmpty(p.TextValue)));
        Assert.IsTrue(patches.Any(p => p.Key == "LocalApiAllowedOrigins" && p.TextValue == "chrome-extension://abc"));
        Assert.IsTrue(patches.Any(p => p.Key == "UiLanguage" && p.TextValue == string.Empty));
    }

    [TestMethod]
    public void BuildPatchesPersistsExplicitUiLanguage()
    {
        var settings = new SnipSettings { UiLanguage = "zh-CN" };
        var patches = SnipSettingsPersister.BuildPatchesFromSettings(settings);
        Assert.IsTrue(patches.Any(p => p.Key == "UiLanguage" && p.TextValue == "zh-CN"));
    }


    [TestMethod]
    public void BuildPatchesIncludesAnnotationBrushSettings()
    {
        var settings = new SnipSettings
        {
            LastStrokeColorHex = "#007AFF",
            LastStrokeWidth = 5.0,
        };

        var patches = SnipSettingsPersister.BuildPatchesFromSettings(settings);
        Assert.IsTrue(patches.Any(p => p.Key == "LastStrokeColorHex" && p.TextValue == "#007AFF"));
        Assert.IsTrue(patches.Any(p => p.Key == "LastStrokeWidth" && p.NumberValue == 5.0));
    }

    [TestMethod]
    public async Task ConcurrentPatchesPreserveAllKeysAndHostFields()
    {
        string path = Path.Combine(Path.GetTempPath(), "snip-settings-concurrent-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {"hostField":42,"plugins":[{"Id":"7667E8C5E74948F7B2D6B5E7CED60783","AdditionalOptions":[]}]}
                """);
            var writes = Enumerable.Range(0, 40).Select(index => Task.Run(() =>
                SnipSettingsPersister.TryPatchPowerToysRunSettings([SnipSettingsPersister.OptionPatch.Number("Key" + index, index)], path)));
            var results = await Task.WhenAll(writes);
            Assert.IsTrue(results.All(success => success));
            var root = JsonNode.Parse(File.ReadAllText(path));
            Assert.AreEqual(42, root["hostField"].GetValue<int>());
            var options = root["plugins"][0]["AdditionalOptions"].AsArray();
            Assert.AreEqual(40, options.Count);
            for (int index = 0; index < 40; index++)
            {
                Assert.AreEqual((double)index, Find(options, "Key" + index)["NumberValue"].GetValue<double>());
            }

            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".snip.*.tmp").Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void QueuedPatchesKeepLatestValueAndExplicitSaveWins()
    {
        string path = Path.Combine(Path.GetTempPath(), "snip-settings-queued-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """
                {"plugins":[{"Id":"7667E8C5E74948F7B2D6B5E7CED60783","AdditionalOptions":[]}]}
                """);
            SnipSettingsPersister.SchedulePersist([SnipSettingsPersister.OptionPatch.Number("Width", 1)], path);
            SnipSettingsPersister.SchedulePersist([SnipSettingsPersister.OptionPatch.Number("Width", 3)], path);
            SnipSettingsPersister.SchedulePersist([SnipSettingsPersister.OptionPatch.Text("Color", "blue")], path);
            SnipSettingsPersister.FlushPending();
            var options = JsonNode.Parse(File.ReadAllText(path))["plugins"][0]["AdditionalOptions"].AsArray();
            Assert.AreEqual(3d, Find(options, "Width")["NumberValue"].GetValue<double>());

            SnipSettingsPersister.SchedulePersist([SnipSettingsPersister.OptionPatch.Number("Width", 4)], path);
            Assert.IsTrue(SnipSettingsPersister.TryPatchPowerToysRunSettings([SnipSettingsPersister.OptionPatch.Number("Width", 5)], path));
            SnipSettingsPersister.FlushPending();
            options = JsonNode.Parse(File.ReadAllText(path))["plugins"][0]["AdditionalOptions"].AsArray();
            Assert.AreEqual(5d, Find(options, "Width")["NumberValue"].GetValue<double>());
            Assert.AreEqual("blue", Find(options, "Color")["TextValue"].GetValue<string>());
        }
        finally
        {
            SnipSettingsPersister.FlushPending();
            File.Delete(path);
        }
    }

    [TestMethod]
    public void FailedBackgroundWriteKeepsPendingValuesForNextFlush()
    {
        string path = Path.Combine(Path.GetTempPath(), "snip-settings-retry-" + Guid.NewGuid().ToString("N") + ".json");
        const string initial = """
            {"plugins":[{"Id":"7667E8C5E74948F7B2D6B5E7CED60783","AdditionalOptions":[]}]}
            """;
        try
        {
            File.WriteAllText(path, "invalid json");
            SnipSettingsPersister.SchedulePersist([SnipSettingsPersister.OptionPatch.Number("Width", 7)], path);
            SnipSettingsPersister.FlushPending();
            Assert.AreEqual("invalid json", File.ReadAllText(path));

            File.WriteAllText(path, initial);
            SnipSettingsPersister.FlushPending();
            var options = JsonNode.Parse(File.ReadAllText(path))["plugins"][0]["AdditionalOptions"].AsArray();
            Assert.AreEqual(7d, Find(options, "Width")["NumberValue"].GetValue<double>());
        }
        finally
        {
            File.WriteAllText(path, initial);
            SnipSettingsPersister.FlushPending();
            File.Delete(path);
        }
    }

    private static JsonNode Find(JsonArray options, string key)
        => options.First(o => o!["Key"]!.GetValue<string>() == key)!;
}
