// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.Models;

/// <summary>State reported to the settings window.</summary>
public readonly record struct LocalApiStatus(bool Enabled, bool Running, int Port, string Error)
{
    /// <summary>Endpoint shown to the user, or null when disabled.</summary>
    public string Address => Enabled && Port > 0 ? $"http://127.0.0.1:{Port}/v1/translate" : null;
}
