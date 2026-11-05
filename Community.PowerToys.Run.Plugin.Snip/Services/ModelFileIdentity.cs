// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>Identifies the local model version for both loaded engines and cached translations.</summary>
internal static class ModelFileIdentity
{
    public static string Build(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "auto";
        }

        try
        {
            var file = new FileInfo(Path.GetFullPath(path.Trim()));
            return file.Exists
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")
                : file.FullName.ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
        {
            return path.Trim().ToUpperInvariant();
        }
    }
}
