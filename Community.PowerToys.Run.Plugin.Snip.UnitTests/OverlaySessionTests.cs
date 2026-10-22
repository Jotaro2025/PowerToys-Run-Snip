// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Community.PowerToys.Run.Plugin.Snip.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class OverlaySessionTests
{
    [TestMethod]
    public void EndsOverlaySession_ForFinishingOutcomes()
    {
        Assert.IsTrue(OverlaySession.EndsOverlaySession(EditOutcome.Cancelled));
        Assert.IsTrue(OverlaySession.EndsOverlaySession(EditOutcome.Saved));
        Assert.IsTrue(OverlaySession.EndsOverlaySession(EditOutcome.Copied));
        Assert.IsTrue(OverlaySession.EndsOverlaySession(EditOutcome.Confirmed));
    }

    [TestMethod]
    public void KeepsOverlaySession_ForOcrTranslatePin()
    {
        Assert.IsTrue(OverlaySession.KeepsOverlaySession(EditOutcome.ExtractText));
        Assert.IsTrue(OverlaySession.KeepsOverlaySession(EditOutcome.TranslateImage));
        Assert.IsTrue(OverlaySession.KeepsOverlaySession(EditOutcome.Pinned));

        Assert.IsFalse(OverlaySession.EndsOverlaySession(EditOutcome.ExtractText));
        Assert.IsFalse(OverlaySession.EndsOverlaySession(EditOutcome.TranslateImage));
        Assert.IsFalse(OverlaySession.EndsOverlaySession(EditOutcome.Pinned));
    }

    [TestMethod]
    public void EndsAndKeeps_AreDisjoint()
    {
        foreach (EditOutcome o in System.Enum.GetValues(typeof(EditOutcome)))
        {
            var ends = OverlaySession.EndsOverlaySession(o);
            var keeps = OverlaySession.KeepsOverlaySession(o);
            Assert.AreNotEqual(ends, keeps, $"Outcome {o} must be exactly one of ends/keeps");
        }
    }
}
