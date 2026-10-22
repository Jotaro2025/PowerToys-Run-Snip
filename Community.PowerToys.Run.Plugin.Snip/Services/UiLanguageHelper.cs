// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Globalization;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Maps <see cref="SnipSettings.UiLanguage"/> to a UI culture and applies it
/// for plugin resource lookups (settings window, Run command titles, etc.).
/// Independent of translator <c>TargetLanguage</c>.
/// </summary>
internal static class UiLanguageHelper
{
    public const string System = "system";
    public const string English = "en";
    public const string ChineseSimplified = "zh-CN";

    /// <summary>
    /// Normalizes a persisted value to <see cref="System"/>, <see cref="English"/>,
    /// or <see cref="ChineseSimplified"/>. Empty / unknown → system.
    /// </summary>
    public static string Normalize(string uiLanguage)
    {
        if (string.IsNullOrWhiteSpace(uiLanguage))
        {
            return System;
        }

        var key = uiLanguage.Trim();
        if (key.Equals(System, StringComparison.OrdinalIgnoreCase)
            || key.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || key.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return System;
        }

        if (key.Equals(English, StringComparison.OrdinalIgnoreCase)
            || key.Equals("en-US", StringComparison.OrdinalIgnoreCase)
            || key.Equals("en-GB", StringComparison.OrdinalIgnoreCase))
        {
            return English;
        }

        if (key.Equals(ChineseSimplified, StringComparison.OrdinalIgnoreCase)
            || key.Equals("zh", StringComparison.OrdinalIgnoreCase)
            || key.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || key.Equals("zh-Hans-CN", StringComparison.OrdinalIgnoreCase))
        {
            return ChineseSimplified;
        }

        return System;
    }

    /// <summary>
    /// Value written to AdditionalOptions: empty for system, otherwise <c>en</c> / <c>zh-CN</c>.
    /// </summary>
    public static string ToPersistedValue(string uiLanguage)
    {
        var normalized = Normalize(uiLanguage);
        return normalized == System ? string.Empty : normalized;
    }

    /// <summary>
    /// Culture name Apply would force, or <c>null</c> when following system
    /// (<see cref="Properties.Resources.Culture"/> cleared).
    /// </summary>
    public static string ResolveCultureName(string uiLanguage)
    {
        return Normalize(uiLanguage) switch
        {
            English => English,
            ChineseSimplified => ChineseSimplified,
            _ => null,
        };
    }

    /// <summary>
    /// Culture Apply would assign to <see cref="Properties.Resources.Culture"/>,
    /// or <c>null</c> for system (follow <see cref="CultureInfo.CurrentUICulture"/>).
    /// </summary>
    public static CultureInfo ResolveCulture(string uiLanguage)
    {
        var name = ResolveCultureName(uiLanguage);
        return name == null ? null : CultureInfo.GetCultureInfo(name);
    }

    /// <summary>
    /// Sets <see cref="Properties.Resources.Culture"/> and the current thread's
    /// <see cref="CultureInfo.CurrentUICulture"/> when an explicit language is chosen.
    /// For system, clears the resource culture override.
    /// </summary>
    public static void Apply(string uiLanguage)
    {
        var culture = ResolveCulture(uiLanguage);
        Properties.Resources.Culture = culture;
        if (culture != null)
        {
            CultureInfo.CurrentUICulture = culture;
        }
    }
}
