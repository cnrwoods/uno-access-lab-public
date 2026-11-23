using System.Text.Json;
using PolicyApi;
using static CheckSuite;

internal static class PolicyStoreChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly PinCredential Credential = new("HMAC-SHA256", "000102030405060708090a0b0c0d0e0f", "ac60ca61a59c150580f7547ac0a581ef85a25edcc2158069c32ca96a8691a79f", "630dcd2966c43366");

    public static async Task RunAsync(CheckSuite suite)
    {
        await suite.RunAsync("Create, list and restart preserve schema 2", async () =>
        {
            var path = suite.FilePath("restart/policies.json");
            var even = new AccessPolicy("POL-001", 1, true, "even");
            var odd = new AccessPolicy("POL-002", 1, false, "odd");
            using (var store = new FilePolicyStore(path))
            {
                Require((await store.GetAllAsync()).Count == 0, "A new store must return an empty list.");
                Require(await store.CreateAsync(odd), "Inactive policy was not created.");
                Require(await store.CreateAsync(even), "Active policy was not created.");
            }
            using var reopened = new FilePolicyStore(path);
            var policies = await reopened.GetAllAsync();
            Require(policies.SequenceEqual(new[] { even, odd }), "Policies changed or lost their ID ordering after restart.");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(policies, JsonOptions));
            var first = json.RootElement[0];
            Require(first.GetProperty("policyId").GetString() == "POL-001" && first.GetProperty("version").GetInt32() == 1 &&
                first.GetProperty("enabled").GetBoolean() && first.GetProperty("allowedMinuteParity").GetString() == "even", "The original JSON fields changed.");
            Require(first.GetProperty("schemaVersion").GetInt32() == 2 && first.GetProperty("accessMode").GetString() == "fingerprintOnly" &&
                first.GetProperty("startMinute").GetInt32() == 0 && first.GetProperty("endMinute").GetInt32() == 1440 && first.GetProperty("pin").ValueKind == JsonValueKind.Null, "Schema 2 defaults changed.");
        });

        await suite.RunAsync("Duplicate IDs cannot replace or activate an existing policy", async () =>
        {
            using var store = new FilePolicyStore(suite.FilePath("duplicates.json"));
            var original = new AccessPolicy("POL-001", 1, false, "even");
            await store.CreateAsync(original);
            Require(!await store.CreateAsync(new AccessPolicy("pol-001", 2, true, "odd")), "Duplicate IDs must be case-insensitive.");
            Require((await store.GetAllAsync()).SequenceEqual(new[] { original }), "Duplicate creation changed the saved policy.");
        });

        await suite.RunAsync("Creating an active policy switches activation and advances the old version", async () =>
        {
            using var store = new FilePolicyStore(suite.FilePath("create-active.json"));
            var old = new AccessPolicy("POL-OLD", 1, true, "any");
            var inactive = new AccessPolicy("POL-INACTIVE", 1, false, "even");
            var next = new AccessPolicy("POL-NEXT", 1, true, "odd");
            await store.CreateAsync(old);
            await store.CreateAsync(inactive);
            var stale = (await store.GetAllAsync()).Single(policy => policy.PolicyId == old.PolicyId);
            await store.CreateAsync(next);
            var policies = await store.GetAllAsync();
            Require(policies.Count(policy => policy.Enabled) == 1 && policies.Single(policy => policy.Enabled).PolicyId == next.PolicyId, "Creating a new active policy must deactivate the old one.");
            Require(policies.Single(policy => policy.PolicyId == old.PolicyId) == (old with { Enabled = false, Version = 2 }), "Deactivation must increment the old version.");
            Require(policies.Single(policy => policy.PolicyId == inactive.PolicyId) == inactive, "Unchanged inactive policy version was modified.");
            Require(stale == old, "A previously returned snapshot was mutated.");
        });

        await suite.RunAsync("Activation, pause and unknown IDs preserve version semantics", async () =>
        {
            var path = suite.FilePath("activation.json");
            using var store = new FilePolicyStore(path);
            await store.CreateAsync(new AccessPolicy("POL-A", 1, false, "any"));
            await store.CreateAsync(new AccessPolicy("POL-B", 1, false, "even"));
            Require(await store.SetActiveAsync("pol-a"), "Activation should match IDs case-insensitively.");
            Require((await store.GetAllAsync()).Single(policy => policy.PolicyId == "POL-A").Version == 2, "Activation did not increment the version.");
            var beforeUnknown = await File.ReadAllTextAsync(path);
            Require(!await store.SetActiveAsync("MISSING"), "Unknown ID should return false.");
            Require(await File.ReadAllTextAsync(path) == beforeUnknown, "Unknown activation changed the file.");
            Require(await store.SetActiveAsync("POL-A"), "Re-selecting the active policy should succeed.");
            Require((await store.GetAllAsync()).Single(policy => policy.PolicyId == "POL-A").Version == 2, "Re-selecting the current active policy should be a no-op.");
            Require(await store.SetActiveAsync("POL-B"), "Second activation failed.");
            var switched = await store.GetAllAsync();
            Require(switched.Single(policy => policy.PolicyId == "POL-A").Version == 3 && switched.Single(policy => policy.PolicyId == "POL-B").Version == 2, "Switching did not version both changed policies.");
            Require(await store.SetActiveAsync(null), "Pause failed.");
            var paused = await store.GetAllAsync();
            Require(paused.All(policy => !policy.Enabled) && paused.Single(policy => policy.PolicyId == "POL-B").Version == 3, "Pause must disable the active policy and advance its version.");
            Require(await store.SetActiveAsync(null), "Repeated pause should succeed.");
            Require((await store.GetAllAsync()).SequenceEqual(paused), "Repeated pause changed versions.");
        });

        await suite.RunAsync("Concurrent creates and activation retain policies with at most one active", async () =>
        {
            using var store = new FilePolicyStore(suite.FilePath("concurrent.json"));
            var created = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => store.CreateAsync(new AccessPolicy($"POL-{i:000}", 1, true, "any"))));
            Require(created.All(result => result), "Distinct concurrent creates were rejected.");
            var policies = await store.GetAllAsync();
            Require(policies.Count == 20 && policies.Count(policy => policy.Enabled) == 1, "Concurrent active creation lost data or enabled multiple policies.");
            Require(policies.All(policy => policy.Version == (policy.Enabled ? 1 : 2)), "Concurrent deactivation versions were lost.");
            var repeated = await Task.WhenAll(Enumerable.Range(1, 12).Select(_ => store.CreateAsync(new AccessPolicy("POL-SHARED", 1, false, "odd"))));
            Require(repeated.Count(result => result) == 1, "Only one concurrent create of a shared ID should succeed.");
            var activated = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => store.SetActiveAsync($"POL-{i:000}")));
            Require(activated.All(result => result), "Concurrent activation failed for an existing ID.");
            policies = await store.GetAllAsync();
            Require(policies.Count == 21 && policies.Count(policy => policy.Enabled) == 1, "Concurrent activation lost policies or enabled multiple policies.");
        });

        await suite.RunAsync("PIN replacement advances the version without changing activation", async () =>
        {
            var path = suite.FilePath("pin-update.json");
            using var store = new FilePolicyStore(path);
            var original = new AccessPolicy("POL-PIN", 1, true, "any") { AccessMode = "fingerprintOrPin", Pin = Credential };
            await store.CreateAsync(original);
            var replacement = Credential with { Salt = "101112131415161718191a1b1c1d1e1f" };
            Require(await store.UpdatePinAsync("pol-pin", replacement), "PIN replacement failed.");
            var updated = (await store.GetAllAsync()).Single();
            Require(updated == (original with { Pin = replacement, Version = 2 }), "PIN replacement changed activation or failed to advance the version.");
            var beforeMissing = await File.ReadAllTextAsync(path);
            Require(!await store.UpdatePinAsync("MISSING", replacement), "Unknown PIN replacement should return false.");
            Require(await File.ReadAllTextAsync(path) == beforeMissing, "Unknown PIN replacement changed the store.");
            Require(original.Version == 1 && original.Pin == Credential, "Updating PIN mutated a previous policy snapshot.");
            await store.CreateAsync(new AccessPolicy("POL-FINGER", 1, false, "any"));
            await ThrowsAsync<ArgumentException>(() => store.UpdatePinAsync("POL-FINGER", replacement));
        });

        await suite.RunAsync("Invalid policy input is rejected without changing stored policies", async () =>
        {
            using var store = new FilePolicyStore(suite.FilePath("validation.json"));
            var original = new AccessPolicy("POL-001", 1, true, "even");
            await store.CreateAsync(original);
            var valid = new AccessPolicy("POL-002", 1, false, "any");
            var invalid = new List<AccessPolicy>();
            invalid.AddRange(new[] { "", " ", "POL 002", "POL/002", "POL.002", "_POL", "-POL", new string('A', 65) }.Select(id => valid with { PolicyId = id }));
            invalid.AddRange(new[] { "", "EVEN", "neither", " odd " }.Select(parity => valid with { AllowedMinuteParity = parity }));
            invalid.AddRange(new[] { 0, -1 }.Select(version => valid with { Version = version }));
            invalid.AddRange(new[] { 0, 1, 3 }.Select(version => valid with { SchemaVersion = version }));
            invalid.AddRange(new[] { "", "FingerprintOnly", "unknown" }.Select(mode => valid with { AccessMode = mode }));
            invalid.Add(valid with { StartMinute = -1 });
            invalid.Add(valid with { EndMinute = 1441 });
            invalid.Add(valid with { StartMinute = 600, EndMinute = 600 });
            invalid.Add(valid with { StartMinute = 1200, EndMinute = 600 });
            invalid.Add(valid with { AccessMode = "pinOnly" });
            invalid.Add(valid with { AccessMode = "fingerprintOrPin" });
            invalid.Add(valid with { Pin = Credential });
            foreach (var credential in new[]
            {
                Credential with { Algorithm = "SHA256" }, Credential with { Salt = "00" },
                Credential with { Salt = Credential.Salt.ToUpperInvariant() }, Credential with { Hash = "00" },
                Credential with { Hash = Credential.Hash.ToUpperInvariant() }, Credential with { KeyId = "00" },
                Credential with { KeyId = Credential.KeyId.ToUpperInvariant() }
            }) invalid.Add(valid with { AccessMode = "pinOnly", Pin = credential });
            foreach (var policy in invalid) await ThrowsAsync<ArgumentException>(() => store.CreateAsync(policy));
            Require((await store.GetAllAsync()).SequenceEqual(new[] { original }), "Invalid input changed the saved policy.");
            Require(await store.CreateAsync(valid with { AccessMode = "pinOnly", Pin = Credential, StartMinute = 1439 }), "The final valid minute of the day should be accepted.");
        });

        await suite.RunAsync("Legacy policies migrate inactive without rewriting the file on read", async () =>
        {
            const string legacy = "[{\"policyId\":\"POL-A\",\"version\":7,\"enabled\":true,\"allowedMinuteParity\":\"even\"},{\"policyId\":\"POL-B\",\"version\":1,\"enabled\":true,\"allowedMinuteParity\":\"odd\"}]";
            var path = suite.FilePath("legacy.json");
            await File.WriteAllTextAsync(path, legacy);
            using var store = new FilePolicyStore(path);
            var migrated = await store.GetAllAsync();
            Require(migrated.Count == 2 && migrated.All(policy => !policy.Enabled && policy.SchemaVersion == 2 && policy.AccessMode == "fingerprintOnly" && policy.Pin is null && policy.StartMinute == 0 && policy.EndMinute == 1440), "Legacy policies must migrate to inactive fingerprint-only schema 2.");
            Require(migrated.Single(policy => policy.PolicyId == "POL-A").Version == 7, "Reading legacy data must not advance its version.");
            Require(await File.ReadAllTextAsync(path) == legacy, "Reading legacy data must not rewrite the file.");
            Require(await store.CreateAsync(new AccessPolicy("POL-C", 1, false, "any")), "Mutation after migration failed.");
            using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Require(stored.RootElement.EnumerateArray().All(item => item.GetProperty("schemaVersion").GetInt32() == 2), "A subsequent mutation did not persist the migrated schema.");
            Require((await store.GetAllAsync()).All(policy => !policy.Enabled), "Legacy migration unexpectedly activated a policy.");
        });

        await suite.RunAsync("Corrupt, partial or conflicting stored policies fail without overwrite", async () =>
        {
            var active = new AccessPolicy("POL-A", 1, true, "any");
            var validJson = JsonSerializer.Serialize(active, JsonOptions);
            var invalidDocuments = new[]
            {
                "not json", "null", "{}", "[null]", "[{}]",
                "[{\"policyId\":\"POL-001\",\"version\":1,\"allowedMinuteParity\":\"even\"}]",
                "[{\"policyId\":\"POL-001\",\"enabled\":true,\"allowedMinuteParity\":\"even\"}]",
                "[{\"version\":1,\"enabled\":true,\"allowedMinuteParity\":\"even\"}]",
                "[{\"policyId\":\"POL-001\",\"version\":1,\"enabled\":true}]",
                "[" + validJson.Replace("\"accessMode\":\"fingerprintOnly\",", "", StringComparison.Ordinal) + "]",
                "[" + validJson.TrimEnd('}') + ",\"plaintextPin\":\"0007\"}]",
                JsonSerializer.Serialize(new[] { active, active with { PolicyId = "POL-B" } }, JsonOptions),
                JsonSerializer.Serialize(new[] { active, active with { PolicyId = "pol-a", Enabled = false } }, JsonOptions),
                JsonSerializer.Serialize(new[] { active with { AccessMode = "pinOnly" } }, JsonOptions),
                JsonSerializer.Serialize(new[] { active with { EndMinute = 0 } }, JsonOptions)
            };
            foreach (var document in invalidDocuments)
            {
                var path = suite.FilePath("corrupt.json");
                await File.WriteAllTextAsync(path, document);
                using var store = new FilePolicyStore(path);
                await ThrowsStoredDataAsync(() => store.GetAllAsync());
                await ThrowsStoredDataAsync(() => store.CreateAsync(new AccessPolicy("POL-NEW", 1, false, "even")));
                await ThrowsStoredDataAsync(() => store.SetActiveAsync(null));
                Require(await File.ReadAllTextAsync(path) == document, "A corrupt file was overwritten.");
            }
        });

        await suite.RunAsync("Cancelled operations leave the store usable and unchanged", async () =>
        {
            using var store = new FilePolicyStore(suite.FilePath("cancellation.json"));
            var original = new AccessPolicy("POL-PIN", 1, true, "any") { AccessMode = "pinOnly", Pin = Credential };
            await store.CreateAsync(original);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => store.GetAllAsync(cancellation.Token));
            await ThrowsAsync<OperationCanceledException>(() => store.CreateAsync(new AccessPolicy("POL-002", 1, true, "odd"), cancellation.Token));
            await ThrowsAsync<OperationCanceledException>(() => store.SetActiveAsync(null, cancellation.Token));
            await ThrowsAsync<OperationCanceledException>(() => store.UpdatePinAsync(original.PolicyId, Credential, cancellation.Token));
            Require((await store.GetAllAsync()).SequenceEqual(new[] { original }), "A cancelled operation changed the store.");
            Require(await store.SetActiveAsync(null), "The store did not recover after cancellation.");
        });
    }
}
