// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Community.PowerToys.Run.Plugin.Snip.Models;

/// <summary>OCR engine used by text extraction and image translate.</summary>
public enum OcrEngineKind
{
    /// <summary>WeChat OCR on x64 when its component is present, otherwise Windows OCR.</summary>
    Auto = 0,

    /// <summary>Bundled / downloaded WeChat OCR component (x64 only).</summary>
    WeChat = 1,

    /// <summary>Built-in <c>Windows.Media.Ocr</c> (works on x64 and ARM64; needs an OCR language).</summary>
    Windows = 2,
}
