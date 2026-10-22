// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.Models;

/// <summary>HyMT2Sharp compute backend selection.</summary>
public enum HyMtBackendKind
{
    /// <summary>Metal → Vulkan → CPU via <c>BackendFactory.Create()</c>.</summary>
    Auto = 0,

    /// <summary>Force CPU kernels.</summary>
    Cpu = 1,

    /// <summary>Force Vulkan.</summary>
    Vulkan = 2,
}
