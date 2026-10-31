// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>Raised when the local engine cannot serve a request (mapped to HTTP 503).</summary>
internal sealed class LocalApiUnavailableException : Exception
{
    public LocalApiUnavailableException(string message)
        : base(message)
    {
    }
}
