// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Community.PowerToys.Run.Plugin.Snip.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public sealed class HdrDisplayStateTests
{
    [TestMethod]
    public void SdrDisplay_DoesNotToneMap()
    {
        HdrDisplayState state = HdrDisplayState.Sdr;
        Assert.IsFalse(state.NeedsToneMapping);
        Assert.AreEqual(1.0, state.WhiteScale, 1e-9);
    }

    [TestMethod]
    public void AdvancedColorEnabledAtNominalWhite_DoesNotToneMap()
    {
        // 1000 raw = 80 nits, scRGB 1.0 is already SDR white.
        var state = new HdrDisplayState(advancedColorEnabled: true, HdrDisplayState.NominalSdrWhiteLevel);
        Assert.IsFalse(state.NeedsToneMapping);
        Assert.AreEqual(1.0, state.WhiteScale, 1e-9);
    }

    [TestMethod]
    public void HdrWithTwoHundredNitSdrWhite_ScalesToFortyPercent()
    {
        // 2500 raw = 200 nits; SDR white sits 2.5x above the 80-nit reference.
        var state = new HdrDisplayState(advancedColorEnabled: true, sdrWhiteLevelRaw: 2500);
        Assert.IsTrue(state.NeedsToneMapping);
        Assert.AreEqual(0.4, state.WhiteScale, 1e-9);
    }

    [TestMethod]
    public void AdvancedColorDisabled_IgnoresWhiteLevel()
    {
        var state = new HdrDisplayState(advancedColorEnabled: false, sdrWhiteLevelRaw: 4000);
        Assert.IsFalse(state.NeedsToneMapping);
        Assert.AreEqual(1.0, state.WhiteScale, 1e-9);
    }

    [TestMethod]
    public void ZeroWhiteLevel_FallsBackToNoScaling()
    {
        var state = new HdrDisplayState(advancedColorEnabled: true, sdrWhiteLevelRaw: 0);
        Assert.IsFalse(state.NeedsToneMapping);
        Assert.AreEqual(1.0, state.WhiteScale, 1e-9);
    }
}
