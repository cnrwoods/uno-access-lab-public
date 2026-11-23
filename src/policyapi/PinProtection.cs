using System.Security.Cryptography;
using System.Text;

namespace PolicyApi;

// The random registration key is shared out of band with one demo UNO. It is
// never included in a policy. This verifier is deliberately not reversible.
public sealed class PinProtection : IDisposable
{
    public const string Algorithm = "HMAC-SHA256";
    public const string Domain = "uno-access-lab/pin/v1";
    private byte[]? sharedKey;

    public PinProtection(string? sharedKeyHex)
    {
        if (string.IsNullOrWhiteSpace(sharedKeyHex)) return;

        var hex = sharedKeyHex.Trim();
        if (hex.Length != 64 || !hex.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("The policy registration key must be exactly 32 bytes encoded as 64 hexadecimal characters.");

        sharedKey = Convert.FromHexString(hex);
        KeyId = Convert.ToHexStringLower(SHA256.HashData(sharedKey))[..16];
    }

    public bool IsConfigured => sharedKey is not null;
    public string? KeyId { get; }

    public static bool IsValidPin(string? pin) =>
        pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);

    public PinCredential Protect(string policyId, string pin)
    {
        if (sharedKey is null)
            throw new InvalidOperationException("Configure the shared registration key before setting a PIN.");
        if (!IsValidPolicyId(policyId)) throw new ArgumentException("Invalid policy ID.", nameof(policyId));
        if (!IsValidPin(pin)) throw new ArgumentException("The PIN must contain 4 to 8 digits.", nameof(pin));

        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var digest = Compute(policyId, salt, pin);
        try
        {
            return new PinCredential(Algorithm, salt, Convert.ToHexStringLower(digest), KeyId!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    public bool Verify(string policyId, string pin, PinCredential? credential)
    {
        if (sharedKey is null || !IsValidPolicyId(policyId) || !IsValidPin(pin) || credential is null ||
            credential.Algorithm != Algorithm || credential.KeyId != KeyId ||
            !IsLowerHex(credential.Salt, 32) || !IsLowerHex(credential.Hash, 64)) return false;

        var actual = Compute(policyId, credential.Salt, pin);
        var expected = Convert.FromHexString(credential.Hash);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private byte[] Compute(string policyId, string salt, string pin)
    {
        // ASCII-compatible UTF-8, lowercase salt HEX TEXT, no trailing newline
        // or NUL. The PIN stays a string so leading zeroes are preserved.
        var message = Encoding.UTF8.GetBytes($"{Domain}\n{policyId}\n{salt}\n{pin}");
        try
        {
            return HMACSHA256.HashData(sharedKey!, message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
        }
    }

    private static bool IsLowerHex(string? value, int length) => value?.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsValidPolicyId(string? id) => id is { Length: >= 1 and <= 64 } &&
        char.IsAsciiLetterOrDigit(id[0]) &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public void Dispose()
    {
        if (sharedKey is not null) CryptographicOperations.ZeroMemory(sharedKey);
        sharedKey = null;
    }
}
