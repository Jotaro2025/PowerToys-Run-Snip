// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.Models;

/// <summary>
/// Classifies edit outcomes for the keep-overlay session lifecycle.
/// OCR / translate / pin keep the snip overlay alive; confirm / copy / save / cancel end it.
/// </summary>
public static class OverlaySession
{
    /// <summary>
    /// True when the outcome should close the fullscreen snip overlay and finish the modal session.
    /// </summary>
    public static bool EndsOverlaySession(EditOutcome outcome)
        => outcome is EditOutcome.Cancelled
            or EditOutcome.Saved
            or EditOutcome.Copied
            or EditOutcome.Confirmed;

    /// <summary>
    /// True when the action opens a side window (OCR / translate / pin) without tearing down the overlay.
    /// </summary>
    public static bool KeepsOverlaySession(EditOutcome outcome)
        => outcome is EditOutcome.ExtractText
            or EditOutcome.TranslateImage
            or EditOutcome.Pinned;
}
