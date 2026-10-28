// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Advanced-color state of one display, expressed in the units the capture
/// pipeline needs: whether the desktop is composed scene-referred in scRGB
/// (HDR, where SDR white sits above the 80-nit reference) and the scale that
/// maps captured scRGB values back to SDR white.
/// </summary>
internal readonly struct HdrDisplayState
{
    /// <summary>Nominal 80-nit reference white in DISPLAYCONFIG_SDR_WHITE_LEVEL units.</summary>
    internal const uint NominalSdrWhiteLevel = 1000;

    internal HdrDisplayState(bool advancedColorEnabled, uint sdrWhiteLevelRaw)
    {
        AdvancedColorEnabled = advancedColorEnabled;
        SdrWhiteLevelRaw = sdrWhiteLevelRaw;
    }

    /// <summary>True when Windows advanced color (HDR/WCG) is active on the display.</summary>
    public bool AdvancedColorEnabled { get; }

    /// <summary>
    /// Raw <c>DISPLAYCONFIG_SDR_WHITE_LEVEL.SDRWhiteLevel</c>: SDR white in
    /// units of 80 nits * 1000 (1000 = 80 nits, 2500 = 200 nits).
    /// </summary>
    public uint SdrWhiteLevelRaw { get; }

    /// <summary>
    /// True when SDR content is composed in scene-referred scRGB white above
    /// the 80-nit reference, so an 8-bit (BGRA8) capture clips and looks
    /// washed out. Requires the FP16 + tone-map path.
    /// </summary>
    public bool NeedsToneMapping => AdvancedColorEnabled && SdrWhiteLevelRaw > NominalSdrWhiteLevel;

    /// <summary>
    /// Multiplier that maps captured scRGB channels back to SDR white
    /// (80 nits / current SDR white). 1.0 when no scaling is needed.
    /// </summary>
    public double WhiteScale => NeedsToneMapping
        ? NominalSdrWhiteLevel / (double)SdrWhiteLevelRaw
        : 1.0;

    /// <summary>SDR display / unknown monitor: capture BGRA8 unchanged.</summary>
    public static HdrDisplayState Sdr => new(advancedColorEnabled: false, NominalSdrWhiteLevel);
}
