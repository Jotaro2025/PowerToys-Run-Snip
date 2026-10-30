// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>How the renderer should reconstruct the erased glyph pixels.</summary>
internal enum MaskFillKind
{
    /// <summary>Predict each pixel from the fitted background plane (flat/gradient).</summary>
    Plane,

    /// <summary>Fill with the sampled flat background color (exact on solid surfaces).</summary>
    Solid,

    /// <summary>Telea-style inpaint (textured/photo backgrounds).</summary>
    Inpaint,
}
