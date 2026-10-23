// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Lightweight GitHub Releases check. Default-off; failures (private repo, rate limit,
/// offline) are silent. Compares <c>tag_name</c> to the installed <c>plugin.json</c> Version.
/// </summary>
public static class UpdateChecker
{
    internal const string DefaultReleasesApi =
        "https://api.github.com/repos/Jotaro2025/PowerToys-Run-Snip/releases/latest";

    private static int _started;

    /// <summary>
    /// Starts at most one background check per process when <paramref name="enabled"/>.
    /// </summary>
    public static void MaybeStart(bool enabled, string releasesApiUrl, Action<string, string> notify)
    {
        if (!enabled || notify == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string local = ReadInstalledVersion() ?? "0.0.0";
                string api = string.IsNullOrWhiteSpace(releasesApiUrl) ? DefaultReleasesApi : releasesApiUrl.Trim();
                var remote = await FetchLatestTagAsync(api).ConfigureAwait(false);
                if (remote == null)
                {
                    return;
                }

                if (!IsNewer(remote, local))
                {
                    return;
                }

                // Resource is "… {0} … {1} …"; avoid CA1863 CompositeFormat ceremony for a one-shot notify.
                string template = Properties.Resources.msg_update_available;
                string title = template.Replace("{0}", remote, StringComparison.Ordinal)
                    .Replace("{1}", local, StringComparison.Ordinal);
                notify(title, "https://github.com/Jotaro2025/PowerToys-Run-Snip/releases");
            }
            catch (Exception ex)
            {
                Log.Exception($"Snip update check failed: {ex.Message}", ex, typeof(UpdateChecker));
            }
        });
    }

    /// <summary>True when <paramref name="remoteTag"/> is a newer SemVer-ish version than <paramref name="localVersion"/>.</summary>
    internal static bool IsNewer(string remoteTag, string localVersion)
    {
        if (!TryParseVersion(remoteTag, out var remote) || !TryParseVersion(localVersion, out var local))
        {
            return false;
        }

        return remote > local;
    }

    /// <summary>Strips a leading <c>v</c>/<c>V</c> and parses major.minor.patch.</summary>
    internal static bool TryParseVersion(string text, out Version version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
        {
            s = s[1..];
        }

        // Ignore pre-release suffixes for comparison (1.4.0-beta → 1.4.0).
        int dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            s = s[..dash];
        }

        return Version.TryParse(s, out version);
    }

    internal static string ReadInstalledVersion()
    {
        try
        {
            string dir = Path.GetDirectoryName(typeof(UpdateChecker).Assembly.Location);
            if (string.IsNullOrEmpty(dir))
            {
                return null;
            }

            string path = Path.Combine(dir, "plugin.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Version", out var v))
            {
                return v.GetString();
            }
        }
        catch (Exception ex)
        {
            Log.Exception($"Snip: read plugin.json version failed: {ex.Message}", ex, typeof(UpdateChecker));
        }

        return null;
    }

    internal static async Task<string> FetchLatestTagAsync(string apiUrl, HttpMessageHandler handler = null)
    {
        HttpMessageHandler own = null;
        var http = handler != null
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient(own = new HttpClientHandler(), disposeHandler: true);
        try
        {
            http.Timeout = TimeSpan.FromSeconds(8);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PowerToys-Run-Snip-UpdateCheck");
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await http.GetAsync(new Uri(apiUrl)).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // Private repo (404), rate limit, etc. — stay quiet.
                Log.Info($"Snip update check HTTP {(int)resp.StatusCode}", typeof(UpdateChecker));
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("tag_name", out var tag))
            {
                return tag.GetString();
            }

            return null;
        }
        finally
        {
            http.Dispose();
            own?.Dispose();
        }
    }
}
