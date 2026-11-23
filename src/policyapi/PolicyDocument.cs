using System.Text.Json;

namespace PolicyApi;

// Both storage providers use the same strict document contract. Invalid data
// fails before any write; legacy records are paused and migrated only on write.
internal static class PolicyDocument
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static AccessPolicy[] Parse(ReadOnlyMemory<byte> json)
    {
        // Match the previous stream parser, including Windows UTF-8 exports.
        if (json.Length >= 3 && json.Span[0] == 0xEF &&
            json.Span[1] == 0xBB && json.Span[2] == 0xBF)
        {
            json = json[3..];
        }
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The policy document must contain a JSON array.");
        }

        var policies = new List<AccessPolicy>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var policy = ParsePolicy(item);
            try
            {
                ValidatePolicy(policy);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("A stored policy contains invalid values.", exception);
            }

            if (!ids.Add(policy.PolicyId))
            {
                throw new InvalidDataException("The policy document contains duplicate policy IDs.");
            }

            policies.Add(policy);
        }

        if (policies.Count(policy => policy.Enabled) > 1)
        {
            throw new InvalidDataException("The policy document contains more than one active policy.");
        }

        return policies.OrderBy(policy => policy.PolicyId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static byte[] Serialize(AccessPolicy[] policies) =>
        JsonSerializer.SerializeToUtf8Bytes(policies, JsonOptions);

    private static AccessPolicy ParsePolicy(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Each stored policy must be a JSON object.");
        }

        var legacy = !item.TryGetProperty("schemaVersion", out _);
        if (legacy)
        {
            RequireFields(item, "policyId", "version", "enabled", "allowedMinuteParity");
        }
        else
        {
            RequireFields(item, "policyId", "version", "enabled", "allowedMinuteParity", "schemaVersion", "accessMode", "startMinute", "endMinute", "pin");
        }

        var id = ReadString(item, "policyId");
        var version = ReadInt(item, "version");
        var enabled = ReadBool(item, "enabled");
        var parity = ReadString(item, "allowedMinuteParity");

        if (legacy)
        {
            if (parity is not ("even" or "odd"))
            {
                throw new InvalidDataException("A legacy policy has invalid minute parity.");
            }

            return new AccessPolicy(id, version, false, parity);
        }

        PinCredential? pin = null;
        var pinElement = item.GetProperty("pin");
        if (pinElement.ValueKind != JsonValueKind.Null)
        {
            RequireFields(pinElement, "algorithm", "salt", "hash", "keyId");
            pin = new PinCredential(ReadString(pinElement, "algorithm"), ReadString(pinElement, "salt"),
                ReadString(pinElement, "hash"), ReadString(pinElement, "keyId"));
        }

        return new AccessPolicy(id, version, enabled, parity, ReadInt(item, "schemaVersion"),
            ReadString(item, "accessMode"), ReadInt(item, "startMinute"), ReadInt(item, "endMinute"), pin);
    }

    private static void RequireFields(JsonElement item, params string[] fields)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Stored policy values have an incorrect structure.");
        }

        var remaining = new HashSet<string>(fields, StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
            {
                throw new InvalidDataException("Stored policy data contains unknown or duplicate fields.");
            }
        }

        if (remaining.Count != 0)
        {
            throw new InvalidDataException("Stored policy data is missing required fields.");
        }
    }

    private static string ReadString(JsonElement item, string name) =>
        item.GetProperty(name).ValueKind == JsonValueKind.String
            ? item.GetProperty(name).GetString()!
            : throw new InvalidDataException("A stored policy field must be a string.");

    private static int ReadInt(JsonElement item, string name)
    {
        var value = item.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new InvalidDataException("A stored policy field must be an integer.");
    }

    private static bool ReadBool(JsonElement item, string name) => item.GetProperty(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidDataException("A stored policy field must be a boolean.")
    };

    internal static void ValidatePolicy(AccessPolicy policy)
    {
        if (string.IsNullOrEmpty(policy.PolicyId) || policy.PolicyId.Length > 64 ||
            !char.IsAsciiLetterOrDigit(policy.PolicyId[0]) ||
            policy.PolicyId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new ArgumentException("Policy ID must start with a letter or digit and contain at most 64 letters, digits, hyphens or underscores.", nameof(policy));
        }

        if (policy.Version < 1)
        {
            throw new ArgumentException("Policy version must be at least 1.", nameof(policy));
        }

        if (policy.SchemaVersion != 2)
        {
            throw new ArgumentException("Policy schema version must be 2.", nameof(policy));
        }

        if (policy.AllowedMinuteParity is not ("any" or "even" or "odd"))
        {
            throw new ArgumentException("Allowed minute parity must be any, even or odd.", nameof(policy));
        }

        if (policy.StartMinute < 0 || policy.StartMinute > 1439 || policy.EndMinute > 1440 || policy.EndMinute <= policy.StartMinute)
        {
            throw new ArgumentException("The daily window must have a start minute from 0 to 1439 and a later end minute up to 1440.", nameof(policy));
        }

        if (policy.AccessMode is not ("fingerprintOnly" or "pinOnly" or "fingerprintOrPin" or "denyAll"))
        {
            throw new ArgumentException("The access mode is not supported.", nameof(policy));
        }

        if (policy.AccessMode is "fingerprintOrPin" or "pinOnly")
        {
            if (policy.Pin is null)
            {
                throw new ArgumentException("A PIN credential is required for this access mode.", nameof(policy));
            }

            ValidatePin(policy.Pin);
        }
        else if (policy.Pin is not null)
        {
            throw new ArgumentException("This access mode must not contain a PIN credential.", nameof(policy));
        }
    }

    internal static void ValidatePin(PinCredential pin)
    {
        if (pin.Algorithm != "HMAC-SHA256" || !IsLowerHex(pin.Salt, 32) ||
            !IsLowerHex(pin.Hash, 64) || !IsLowerHex(pin.KeyId, 16))
        {
            throw new ArgumentException("The PIN credential must use HMAC-SHA256 with correctly sized lowercase hexadecimal salt, hash and key ID.", nameof(pin));
        }
    }

    private static bool IsLowerHex(string? value, int length) => value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
