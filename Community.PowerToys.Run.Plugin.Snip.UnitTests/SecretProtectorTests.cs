// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

using Community.PowerToys.Run.Plugin.Snip.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Community.PowerToys.Run.Plugin.Snip.UnitTests;

[TestClass]
public class SecretProtectorTests
{
    [TestMethod]
    public void ProtectThenUnprotectRoundTrips()
    {
        const string key = "sk-test-1234567890-密钥";
        string stored = SecretProtector.Protect(key);

        Assert.IsTrue(SecretProtector.IsProtected(stored));
        Assert.IsFalse(stored.Contains("sk-test", StringComparison.Ordinal), "Ciphertext must not contain the plaintext.");
        Assert.IsFalse(SecretProtector.NeedsMigration(stored));
        Assert.AreEqual(key, SecretProtector.Unprotect(stored));
    }

    [TestMethod]
    public void ProtectIsIdempotentAndKeepsEmpty()
    {
        string stored = SecretProtector.Protect("abc");
        Assert.AreEqual(stored, SecretProtector.Protect(stored));
        Assert.AreEqual(string.Empty, SecretProtector.Protect(string.Empty));
        Assert.AreEqual(string.Empty, SecretProtector.Protect(null));
    }

    [TestMethod]
    public void LegacyPlaintextPassesThroughAndNeedsMigration()
    {
        Assert.AreEqual("plain-key", SecretProtector.Unprotect("plain-key"));
        Assert.IsTrue(SecretProtector.NeedsMigration("plain-key"));
        Assert.IsFalse(SecretProtector.NeedsMigration(string.Empty));
        Assert.IsFalse(SecretProtector.NeedsMigration(null));
        Assert.AreEqual(string.Empty, SecretProtector.Unprotect(null));
    }

    [TestMethod]
    public void UndecryptableValueYieldsEmpty()
    {
        // Valid Base64 but not a DPAPI blob for this user (e.g. settings restored from another machine).
        Assert.AreEqual(string.Empty, SecretProtector.Unprotect(SecretProtector.Prefix + Convert.ToBase64String(new byte[64])));
        Assert.AreEqual(string.Empty, SecretProtector.Unprotect(SecretProtector.Prefix + "not base64!"));
    }

    [TestMethod]
    public void PersistedSettingsStoreProtectedApiKey()
    {
        var settings = new SnipSettings { HttpApiKey = "sk-roundtrip" };
        var patch = SnipSettingsPersister.BuildPatchesFromSettings(settings).Single(p => p.Key == "HttpApiKey");
        Assert.IsTrue(SecretProtector.IsProtected(patch.TextValue));

        var path = Path.Combine(Path.GetTempPath(), "snip-secret-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            { "plugins": [ { "Id": "7667E8C5E74948F7B2D6B5E7CED60783", "AdditionalOptions": [ { "Key": "HttpApiKey", "TextValue": "sk-roundtrip" } ] } ] }
            """);
        try
        {
            Assert.IsTrue(SnipSettingsPersister.TryPatchPowerToysRunSettings([patch], path));
            string text = File.ReadAllText(path);
            Assert.IsFalse(text.Contains("sk-roundtrip", StringComparison.Ordinal), "Plaintext key must be gone from settings.json.");

            var stored = JsonNode.Parse(text)!["plugins"]![0]!["AdditionalOptions"]!.AsArray()
                .First(o => (string)o!["Key"] == "HttpApiKey")!["TextValue"]!.GetValue<string>();
            Assert.AreEqual("sk-roundtrip", SecretProtector.Unprotect(stored));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
