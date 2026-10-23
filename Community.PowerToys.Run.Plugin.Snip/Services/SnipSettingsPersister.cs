// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Patches this plugin's <c>AdditionalOptions</c> inside PowerToys Run
/// <c>settings.json</c> so the custom settings window stays in sync with the
/// PowerToys Settings page after restart.
/// </summary>
internal static class SnipSettingsPersister
{
    public const string PluginId = HyMtGgufPathPersister.PluginId;

    public enum OptionValueKind
    {
        Text,
        Bool,
        Number,
        ComboBox,
    }

    /// <summary>One AdditionalOptions field to upsert.</summary>
    public readonly struct OptionPatch
    {
        public OptionPatch(string key, OptionValueKind kind, string textValue = null, bool? boolValue = null, double? numberValue = null, int? comboBoxValue = null)
        {
            Key = key;
            Kind = kind;
            TextValue = textValue;
            BoolValue = boolValue;
            NumberValue = numberValue;
            ComboBoxValue = comboBoxValue;
        }

        public string Key { get; }

        public OptionValueKind Kind { get; }

        public string TextValue { get; }

        public bool? BoolValue { get; }

        public double? NumberValue { get; }

        public int? ComboBoxValue { get; }

        public static OptionPatch Text(string key, string value)
            => new OptionPatch(key, OptionValueKind.Text, textValue: value ?? string.Empty);

        public static OptionPatch Bool(string key, bool value)
            => new OptionPatch(key, OptionValueKind.Bool, boolValue: value);

        public static OptionPatch Number(string key, double value)
            => new OptionPatch(key, OptionValueKind.Number, numberValue: value);

        public static OptionPatch ComboBox(string key, int value)
            => new OptionPatch(key, OptionValueKind.ComboBox, comboBoxValue: value);
    }

    public static string PowerToysRunSettingsPath => HyMtGgufPathPersister.PowerToysRunSettingsPath;

    /// <summary>
    /// Writes all <paramref name="patches"/> into the plugin's AdditionalOptions.
    /// When a HyMtGgufPath text patch is present, also refreshes the LocalAppData sidecar.
    /// </summary>
    /// <returns>True when the PowerToys settings.json patch succeeded.</returns>
    public static bool Persist(IEnumerable<OptionPatch> patches, string settingsFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(patches);
        var list = patches as IList<OptionPatch> ?? new List<OptionPatch>(patches);
        if (list.Count == 0)
        {
            return false;
        }

        foreach (var p in list)
        {
            if (p.Kind == OptionValueKind.Text
                && string.Equals(p.Key, HyMtGgufPathPersister.GgufPathOptionKey, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(p.TextValue))
            {
                HyMtGgufPathPersister.WriteSidecar(p.TextValue.Trim());
            }
        }

        return TryPatchPowerToysRunSettings(list, settingsFilePath ?? PowerToysRunSettingsPath);
    }

    /// <summary>Builds patches covering every field of <paramref name="settings"/>.</summary>
    public static IReadOnlyList<OptionPatch> BuildPatchesFromSettings(SnipSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return
        [
            OptionPatch.Text("SaveDirectory", settings.SaveDirectory ?? string.Empty),
            OptionPatch.Bool("CopyAfterCapture", settings.CopyAfterCapture),
            OptionPatch.Bool("AutoSaveAfterCapture", settings.AutoSaveAfterCapture),
            OptionPatch.Number("CaptureDelayMs", settings.CaptureDelayMs),
            OptionPatch.Number("ScrollIntervalMs", settings.ScrollIntervalMs),
            OptionPatch.Number("ScrollMaxTiles", settings.ScrollMaxTiles),
            OptionPatch.Text("GlobalHotkey", settings.GlobalHotkey ?? string.Empty),
            OptionPatch.ComboBox("OcrEngine", (int)settings.OcrEngine),
            OptionPatch.Text("WindowsOcrLanguage", string.IsNullOrWhiteSpace(settings.WindowsOcrLanguage) ? "auto" : settings.WindowsOcrLanguage),
            OptionPatch.ComboBox("TranslatorProvider", (int)settings.TranslatorProvider),
            OptionPatch.Text("TargetLanguage", settings.TargetLanguage ?? string.Empty),
            OptionPatch.Text("UiLanguage", UiLanguageHelper.ToPersistedValue(settings.UiLanguage)),
            OptionPatch.Text("HttpBaseUrl", settings.HttpBaseUrl ?? string.Empty),
            OptionPatch.Text("HttpApiKey", SecretProtector.Protect(settings.HttpApiKey ?? string.Empty)),
            OptionPatch.Text("HttpModel", settings.HttpModel ?? string.Empty),
            OptionPatch.Text("HyMtGgufPath", settings.HyMtGgufPath ?? string.Empty),
            OptionPatch.Text("HyMtCustomDownloadUrl", settings.HyMtCustomDownloadUrl ?? string.Empty),
            OptionPatch.Number("HyMtIdleUnloadMinutes", settings.HyMtIdleUnloadMinutes),
            OptionPatch.ComboBox("HyMtBackend", (int)settings.HyMtBackend),
            OptionPatch.Bool("DownloadRecommendedHyMtModel", false),
            OptionPatch.Text("LastStrokeColorHex", string.IsNullOrWhiteSpace(settings.LastStrokeColorHex) ? "#EE204D" : settings.LastStrokeColorHex),
            OptionPatch.Number("LastStrokeWidth", settings.LastStrokeWidth > 0 ? settings.LastStrokeWidth : 3.0),
        ];
    }

    /// <summary>Used by tests with a temp file.</summary>
    internal static bool TryPatchPowerToysRunSettings(IEnumerable<OptionPatch> patches, string settingsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);
        ArgumentNullException.ThrowIfNull(patches);

        try
        {
            if (!File.Exists(settingsFilePath))
            {
                Log.Info(
                    $"Snip settings persist: PowerToys Run settings not found at {settingsFilePath}; sidecar only if any.",
                    typeof(SnipSettingsPersister));
                return false;
            }

            var json = File.ReadAllText(settingsFilePath).Trim('\0');
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null)
            {
                return false;
            }

            var plugins = FindArray(root, "plugins", "Plugins");
            if (plugins == null)
            {
                return false;
            }

            JsonObject pluginObj = null;
            foreach (var node in plugins)
            {
                if (node is not JsonObject po)
                {
                    continue;
                }

                var id = GetString(po, "id", "Id");
                if (string.Equals(id, PluginId, StringComparison.OrdinalIgnoreCase))
                {
                    pluginObj = po;
                    break;
                }
            }

            if (pluginObj == null)
            {
                Log.Info("Snip settings persist: plugin entry not in settings.json yet.", typeof(SnipSettingsPersister));
                return false;
            }

            var options = FindArray(pluginObj, "additionalOptions", "AdditionalOptions");
            if (options == null)
            {
                options = new JsonArray();
                pluginObj["AdditionalOptions"] = options;
            }

            foreach (var patch in patches)
            {
                if (string.IsNullOrWhiteSpace(patch.Key))
                {
                    continue;
                }

                UpsertOption(options, patch);
            }

            var tmp = settingsFilePath + ".snip.tmp";
            var written = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, written);
            File.Copy(tmp, settingsFilePath, overwrite: true);
            TryDelete(tmp);
            Log.Info("Snip settings persisted to PowerToys Run settings.json", typeof(SnipSettingsPersister));
            return true;
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip settings persist to settings.json failed: {ex.Message}", ex, typeof(SnipSettingsPersister));
            return false;
        }
    }

    private static void UpsertOption(JsonArray options, OptionPatch patch)
    {
        JsonObject target = null;
        foreach (var node in options)
        {
            if (node is JsonObject opt
                && string.Equals(GetString(opt, "key", "Key"), patch.Key, StringComparison.Ordinal))
            {
                target = opt;
                break;
            }
        }

        if (target == null)
        {
            target = new JsonObject { ["Key"] = patch.Key };
            options.Add(target);
        }

        switch (patch.Kind)
        {
            case OptionValueKind.Text:
                target["TextValue"] = patch.TextValue ?? string.Empty;
                break;
            case OptionValueKind.Bool:
                target["Value"] = patch.BoolValue ?? false;
                break;
            case OptionValueKind.Number:
                target["NumberValue"] = patch.NumberValue ?? 0;
                break;
            case OptionValueKind.ComboBox:
                target["ComboBoxValue"] = patch.ComboBoxValue ?? 0;
                break;
        }
    }

    private static JsonArray FindArray(JsonObject obj, params string[] names)
    {
        foreach (var n in names)
        {
            if (obj[n] is JsonArray arr)
            {
                return arr;
            }
        }

        return null;
    }

    private static string GetString(JsonObject obj, params string[] names)
    {
        foreach (var n in names)
        {
            if (obj[n] is JsonValue v && v.TryGetValue<string>(out var s))
            {
                return s;
            }
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
