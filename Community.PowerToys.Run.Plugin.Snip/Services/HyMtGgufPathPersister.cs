// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Persists the Hy-MT GGUF path into PowerToys Run <c>settings.json</c> (real PT store)
/// and a small sidecar next to the cache so restarts still resolve the path.
/// Multi-key patches live in <see cref="SnipSettingsPersister"/>.
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
        return SnipSettingsPersister.TryPatchPowerToysRunSettings(
            [
                SnipSettingsPersister.OptionPatch.Text(GgufPathOptionKey, ggufPath),
                SnipSettingsPersister.OptionPatch.Bool(DownloadOptionKey, false),
            ],
            settingsFilePath);
    }
}
