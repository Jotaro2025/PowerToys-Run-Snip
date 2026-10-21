// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Text extraction failed for a reason that can be shown to the user as is
/// (the message is already localized).
/// </summary>
public sealed class OcrException : Exception
{
    public OcrException()
    {
    }

    public OcrException(string message)
        : base(message)
    {
    }

    public OcrException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
