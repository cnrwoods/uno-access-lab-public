namespace PolicyApi;

public sealed record AccessPolicy(
    string PolicyId,
    int Version,
    bool Enabled,
    string AllowedMinuteParity,
    int SchemaVersion = 2,
    string AccessMode = "fingerprintOnly",
    int StartMinute = 0,
    int EndMinute = 1440,
    PinCredential? Pin = null);

public sealed record PinCredential(string Algorithm, string Salt, string Hash, string KeyId);
