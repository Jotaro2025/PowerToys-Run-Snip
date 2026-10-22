// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Persists the Hy-MT GGUF path into PowerToys Run <c>settings.json</c> (real PT store)
/// and a small sidecar next to the cache so restarts still resolve the path.
/// </summary>
internal static class HyMtGgufPathPersister
{
    public const string PluginId = "7667E8C5E74948F7B2D6B5E7CED60783";
    public const string GgufPathOptionKey = "HyMtGgufPath";
    public const string DownloadOptionKey = "DownloadRecommendedHyMtModel";

    /// <summary>Sidecar file written next to the cached GGUF (<c>….gguf.path</c>).</summary>
    public static string DefaultSidecarPath => HyMtModelDownloader.DefaultCachePath + ".path";

    /// <summary>
    /// PowerToys Run settings file:
    /// <c>%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\settings.json</c>.
    /// </summary>
    public static string PowerToysRunSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft",
        "PowerToys",
        "PowerToys Run",
        "settings.json");

    /// <summary>
    /// Writes <paramref name="ggufPath"/> to the PT Run plugin option, clears the one-shot
    /// download checkbox in JSON when present, and updates the LocalAppData sidecar.
    /// </summary>
    /// <returns>True when the PowerToys settings.json patch succeeded.</returns>
    public static bool Persist(string ggufPath, string settingsFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(ggufPath))
        {
            return false;
        }

        WriteSidecar(ggufPath.Trim());
        return TryPatchPowerToysRunSettings(ggufPath.Trim(), settingsFilePath ?? PowerToysRunSettingsPath);
    }

    /// <summary>Reads a previously persisted sidecar path when the file still looks valid.</summary>
    public static string TryReadSidecarPath()
    {
        try
        {
            var sidecar = DefaultSidecarPath;
            if (!File.Exists(sidecar))
            {
                return null;
            }

            var path = File.ReadAllText(sidecar).Trim();
            return HyMtModelDownloader.IsValidCachedFile(path) ? path : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void WriteSidecar(string ggufPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(DefaultSidecarPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(DefaultSidecarPath, ggufPath);
        }
        catch (Exception ex)
        {
            Log.Warn($"Snip HyMT path sidecar write failed: {ex.Message}", typeof(HyMtGgufPathPersister));
        }
    }

    /// <summary>Patches <c>plugins[].AdditionalOptions</c> for this plugin. Used by tests with a temp file.</summary>
    internal static bool TryPatchPowerToysRunSettings(string ggufPath, string settingsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFilePath);
        try
        {
            if (!File.Exists(settingsFilePath))
            {
                Log.Info(
                    $"Snip HyMT path persist: PowerToys Run settings not found at {settingsFilePath}; sidecar only.",
                    typeof(HyMtGgufPathPersister));
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
                Log.Info("Snip HyMT path persist: plugin entry not in settings.json yet; sidecar only.", typeof(HyMtGgufPathPersister));
                return false;
            }

            var options = FindArray(pluginObj, "additionalOptions", "AdditionalOptions");
            if (options == null)
            {
                options = new JsonArray();
                pluginObj["AdditionalOptions"] = options;
            }

            UpsertTextOption(options, GgufPathOptionKey, ggufPath);
            UpsertBoolOption(options, DownloadOptionKey, false);

            var tmp = settingsFilePath + ".snip.tmp";
            var written = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, written);
            File.Copy(tmp, settingsFilePath, overwrite: true);
            TryDelete(tmp);
            Log.Info($"Snip HyMT path persisted to PowerToys Run settings → {ggufPath}", typeof(HyMtGgufPathPersister));
            return true;
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip HyMT path persist to settings.json failed: {ex.Message}", ex, typeof(HyMtGgufPathPersister));
            return false;
        }
    }

    private static void UpsertTextOption(JsonArray options, string key, string textValue)
    {
        foreach (var node in options)
        {
            if (node is JsonObject opt && string.Equals(GetString(opt, "key", "Key"), key, StringComparison.Ordinal))
            {
                opt["TextValue"] = textValue;
                return;
            }
        }

        options.Add(new JsonObject
        {
            ["Key"] = key,
            ["TextValue"] = textValue,
        });
    }

    private static void UpsertBoolOption(JsonArray options, string key, bool value)
    {
        foreach (var node in options)
        {
            if (node is JsonObject opt && string.Equals(GetString(opt, "key", "Key"), key, StringComparison.Ordinal))
            {
                opt["Value"] = value;
                return;
            }
        }

        options.Add(new JsonObject
        {
            ["Key"] = key,
            ["Value"] = value,
        });
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
