// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.Models;

/// <summary>
/// Compute backend for the built-in PaddleOCR engine
/// (<c>Sdcb.SimdPaddleOCR</c>).
/// </summary>
public enum OcrBackendKind
{
    /// <summary>
    /// Let the library pick: GPU (Vulkan/Metal) when a device is available and
    /// benchmarks faster than CPU, otherwise CPU.
    /// </summary>
    Auto = 0,

    /// <summary>Force the managed SIMD CPU kernels (most stable, no graphics driver).</summary>
    Cpu = 1,

    /// <summary>
    /// Force Vulkan (uses the system <c>vulkan-1.dll</c>). Measured ~8× faster
    /// than CPU on a full-screen capture with a discrete AMD GPU.
    /// </summary>
    Vulkan = 2,
}
