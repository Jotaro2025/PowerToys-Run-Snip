// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Security.Cryptography;
using System.Text;

using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.Snip.Services;

/// <summary>
/// Encrypts secrets (the HTTP translator API key) for storage in PowerToys Run
/// <c>settings.json</c> with Windows DPAPI, scope <see cref="DataProtectionScope.CurrentUser"/>.
/// Stored form: <c>dpapi:v1:</c> + Base64 ciphertext. Values without the prefix are legacy
/// plaintext and are accepted as-is (and re-saved encrypted).
/// </summary>
/// <remarks>
/// DPAPI ties the ciphertext to this Windows user on this machine: a settings.json restored on
/// another machine / user cannot be decrypted, and the key must be entered again.
/// </remarks>
internal static class SecretProtector
{
    /// <summary>Marker prefix of an encrypted value.</summary>
    public const string Prefix = "dpapi:v1:";

    // Optional DPAPI entropy: scopes ciphertexts to this purpose.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Community.PowerToys.Run.Plugin.Snip/HttpApiKey/v1");

    /// <summary>True when <paramref name="stored"/> carries the encrypted-value marker.</summary>
    public static bool IsProtected(string stored)
        => stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>True when a stored value is non-empty legacy plaintext that should be re-saved encrypted.</summary>
    public static bool NeedsMigration(string stored)
        => !string.IsNullOrEmpty(stored) && !IsProtected(stored);

    /// <summary>
    /// Returns the storage form of <paramref name="plain"/>. Empty stays empty; an already
    /// protected value is returned unchanged (idempotent).
    /// </summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain))
        {
            return plain ?? string.Empty;
        }

        byte[] data = Encoding.UTF8.GetBytes(plain);
        try
        {
            byte[] cipher = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(cipher);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <summary>
    /// Returns the plaintext of a stored value. Legacy plaintext is returned unchanged.
    /// A protected value that cannot be decrypted (other machine / user, corrupted) yields
    /// an empty string and a warning in the plugin log.
    /// </summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return string.Empty;
        }

        if (!IsProtected(stored))
        {
            return stored;
        }

        try
        {
            byte[] cipher = Convert.FromBase64String(stored.Substring(Prefix.Length));
            byte[] data = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(data);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(data);
            }
        }
        catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
        {
            Log.Warn(
                $"Snip: stored HTTP API key could not be decrypted (settings from another machine or user?); re-enter it. {ex.Message}",
                typeof(SecretProtector));
            return string.Empty;
        }
    }
}
