// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;

namespace Community.PowerToys.Run.Plugin.Snip.Core;

/// <summary>
/// Factory returning the best available <see cref="ICaptureService"/> for the current OS.
/// </summary>
public static class CaptureFactory
{
    public static ICaptureService Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new Win32CaptureService();
        }

        throw new PlatformNotSupportedException(
            $"Screen capture is not implemented for {System.Runtime.InteropServices.RuntimeInformation.OSDescription}.");
    }
}
