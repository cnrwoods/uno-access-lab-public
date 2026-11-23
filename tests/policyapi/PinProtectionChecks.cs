using System.Text.Json;
using PolicyApi;
using static CheckSuite;

internal static class PinProtectionChecks
{
    // PUBLIC TEST FIXTURES ONLY. Never configure a deployed app/device with this key.
    private const string TestKey = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    private static readonly PinCredential KnownCredential = new("HMAC-SHA256", "000102030405060708090a0b0c0d0e0f", "ac60ca61a59c150580f7547ac0a581ef85a25edcc2158069c32ca96a8691a79f", "630dcd2966c43366");

    public static async Task RunAsync(CheckSuite suite)
    {
        await suite.RunAsync("PIN verifier matches the published independent interoperability vector", () =>
        {
            using var protection = new PinProtection(TestKey);
            Require(protection.IsConfigured && protection.KeyId == KnownCredential.KeyId, "The key ID does not match the SHA-256 test vector.");
            Require(protection.Verify("POL-001", "0007", KnownCredential), "The HMAC does not match the independent test vector.");
            Require(!protection.Verify("POL-001", "0008", KnownCredential), "A wrong PIN was accepted.");
            Require(!protection.Verify("POL-001", "7", KnownCredential), "Leading zeroes were discarded.");
            Require(!protection.Verify("POL-002", "0007", KnownCredential), "A PIN credential was accepted for a different policy ID.");
            Require(!protection.Verify("pol-001", "0007", KnownCredential), "PIN verification must use the exact policy ID supplied in the document.");
            using var wrongKey = new PinProtection(new string('f', 64));
            Require(!wrongKey.Verify("POL-001", "0007", KnownCredential), "A credential from another registration key was accepted.");
            using var uppercase = new PinProtection("  " + TestKey.ToUpperInvariant() + "  ");
            Require(uppercase.Verify("POL-001", "0007", KnownCredential), "Key text normalization changed the decoded key.");
            return Task.CompletedTask;
        });

        await suite.RunAsync("PIN creation uses distinct salts and serializes no plaintext or registration key", () =>
        {
            using var protection = new PinProtection(TestKey);
            var credentials = Enumerable.Range(0, 16).Select(_ => protection.Protect("POL-001", "0007")).ToArray();
            Require(credentials.Select(item => item.Salt).Distinct(StringComparer.Ordinal).Count() == credentials.Length, "Repeated PIN creation reused a salt.");
            foreach (var credential in credentials)
            {
                Require(protection.Verify("POL-001", "0007", credential), "A generated PIN credential could not be verified.");
                Require(!protection.Verify("POL-001", "7000", credential), "A generated credential accepted a wrong PIN.");
                var serialized = JsonSerializer.Serialize(credential, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                using var document = JsonDocument.Parse(serialized);
                var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
                Require(propertyNames.SequenceEqual(new[] { "algorithm", "hash", "keyId", "salt" }), "Credential JSON contains unexpected fields.");
                Require(!serialized.Contains("\"0007\"", StringComparison.Ordinal) && !serialized.Contains(TestKey, StringComparison.Ordinal), "Plaintext PIN or shared registration key was serialized.");
                Require(credential.Salt.Length == 32 && credential.Hash.Length == 64 && credential.KeyId.Length == 16, "Credential lengths differ from the device contract.");
            }
            return Task.CompletedTask;
        });

        await suite.RunAsync("Only four to eight ASCII PIN digits are accepted", async () =>
        {
            using var protection = new PinProtection(TestKey);
            foreach (var pin in new[] { "0000", "01234567", "12345", "123456" })
            {
                Require(PinProtection.IsValidPin(pin), "A valid PIN was rejected.");
                Require(protection.Verify("POL-001", pin, protection.Protect("POL-001", pin)), "A valid PIN did not round trip.");
            }
            foreach (var pin in new[] { null, "", "123", "123456789", " 1234", "1234 ", "12 34", "12a4", "１２３４", "١٢٣٤", "1234\n" })
            {
                Require(!PinProtection.IsValidPin(pin), "Invalid PIN text was accepted.");
                await ThrowsAsync<ArgumentException>(() => { protection.Protect("POL-001", pin!); return Task.CompletedTask; });
                Require(!protection.Verify("POL-001", pin!, KnownCredential), "Invalid PIN text was verified.");
            }
            foreach (var id in new[] { "", "BAD/ID", "_BAD", "POL\n001", new string('a', 65) })
            {
                await ThrowsAsync<ArgumentException>(() => { protection.Protect(id, "0007"); return Task.CompletedTask; });
                Require(!protection.Verify(id, "0007", KnownCredential), "An invalid policy ID was verified.");
            }
        });

        await suite.RunAsync("PIN verification rejects malformed credentials and missing keys", async () =>
        {
            using var protection = new PinProtection(TestKey);
            foreach (var credential in new PinCredential?[]
            {
                null, KnownCredential with { Algorithm = "SHA256" }, KnownCredential with { KeyId = "wrong" },
                KnownCredential with { Salt = "00" }, KnownCredential with { Salt = KnownCredential.Salt.ToUpperInvariant() },
                KnownCredential with { Hash = "00" }, KnownCredential with { Hash = new string('g', 64) },
                KnownCredential with { Hash = KnownCredential.Hash.ToUpperInvariant() }
            }) Require(!protection.Verify("POL-001", "0007", credential), "Malformed credential metadata was accepted.");
            foreach (var key in new string?[] { null, "", "  " })
            {
                using var unconfigured = new PinProtection(key);
                Require(!unconfigured.IsConfigured && unconfigured.KeyId is null && !unconfigured.Verify("POL-001", "0007", KnownCredential), "An unconfigured registration key was usable.");
                await ThrowsAsync<InvalidOperationException>(() => { unconfigured.Protect("POL-001", "0007"); return Task.CompletedTask; });
            }
            foreach (var key in new[] { "00", new string('g', 64), new string('0', 63), new string('0', 65) })
                await ThrowsAsync<InvalidOperationException>(() => { using var invalid = new PinProtection(key); return Task.CompletedTask; });
            protection.Dispose();
            Require(!protection.IsConfigured && !protection.Verify("POL-001", "0007", KnownCredential), "A disposed registration key was usable.");
        });
    }
}
