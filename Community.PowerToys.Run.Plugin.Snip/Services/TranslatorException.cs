// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>User-facing failure while configuring or running a translator.</summary>
public sealed class TranslatorException : Exception
{
    public TranslatorException()
    {
    }

    public TranslatorException(string message)
        : base(message)
    {
    }

    public TranslatorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
