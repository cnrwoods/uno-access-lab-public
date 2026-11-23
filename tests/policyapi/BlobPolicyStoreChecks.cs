using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using PolicyApi;
using static CheckSuite;

internal static class BlobPolicyStoreChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly PinCredential Credential = new("HMAC-SHA256", "000102030405060708090a0b0c0d0e0f", "ac60ca61a59c150580f7547ac0a581ef85a25edcc2158069c32ca96a8691a79f", "630dcd2966c43366");

    public static async Task RunAsync(CheckSuite suite)
    {
        await suite.RunAsync("Blob missing document is empty and no-op operations do not create it", async () =>
        {
            using var service = new MemoryBlobService();
            var store = service.OpenStore();
            Require((await store.GetAllAsync()).Count == 0, "A missing blob must be an empty store.");
            Require(!await store.RemoveAsync("MISSING"), "Missing removal must return false.");
            Require(!await store.SetActiveAsync("MISSING"), "Missing activation must return false.");
            Require(await store.SetActiveAsync(null), "Pausing an empty store must succeed.");
            Require(service.WriteRequests == 0 && service.StoredBytes is null, "No-op operations unexpectedly created a blob.");
        });

        await suite.RunAsync("Blob restart, PIN changes, activation and removal preserve the JSON contract", async () =>
        {
            using var service = new MemoryBlobService();
            var store = service.OpenStore();
            var pinPolicy = new AccessPolicy("POL-PIN", 1, true, "any")
            {
                AccessMode = "fingerprintOrPin", Pin = Credential, StartMinute = 540, EndMinute = 1020
            };
            var fingerprint = new AccessPolicy("POL-FINGER", 1, false, "odd");
            Require(await store.CreateAsync(pinPolicy), "PIN policy creation failed.");
            Require(await store.CreateAsync(fingerprint), "Fingerprint policy creation failed.");
            Require(!await store.CreateAsync(pinPolicy with { PolicyId = "pol-pin", Enabled = false }), "Duplicate IDs must not overwrite a blob policy.");
            var reopened = service.OpenStore();
            Require((await reopened.GetAllAsync()).SequenceEqual(new[] { fingerprint, pinPolicy }), "A new client did not read the same sorted policies and PIN verifier.");
            Require(await reopened.SetActiveAsync("pol-finger"), "Activation should be case-insensitive.");
            var activated = await reopened.GetAllAsync();
            Require(activated.Single(policy => policy.PolicyId == "POL-FINGER") == (fingerprint with { Enabled = true, Version = 2 }), "Activation/version was not persisted.");
            Require(activated.Single(policy => policy.PolicyId == "POL-PIN") == (pinPolicy with { Enabled = false, Version = 2 }), "Previous activation/version was not persisted.");
            var writes = service.WriteRequests;
            Require(await reopened.SetActiveAsync("POL-FINGER"), "Selecting the active policy should succeed.");
            Require(!await reopened.RemoveAsync("MISSING"), "Missing removal should return false.");
            Require(service.WriteRequests == writes, "Idempotent/missing mutations rewrote the blob.");
            var replacement = Credential with { Salt = "101112131415161718191a1b1c1d1e1f" };
            Require(await reopened.UpdatePinAsync("pol-pin", replacement), "PIN replacement failed.");
            Require(await reopened.RemoveAsync("POL-FINGER"), "Removing the active policy failed.");
            var remaining = (await service.OpenStore().GetAllAsync()).Single();
            Require(remaining == (pinPolicy with { Enabled = false, Version = 3, Pin = replacement }), "PIN/removal changed unrelated state or enabled a survivor.");
            Require(service.AllWritesConditional && service.JsonContentTypesOnly, "Writes must include a compare-and-swap condition and JSON content type.");

            var path = suite.FilePath("blob-roundtrip.json");
            await File.WriteAllBytesAsync(path, service.StoredBytes!);
            using var fileStore = new FilePolicyStore(path);
            Require((await fileStore.GetAllAsync()).Single() == remaining, "Blob output is incompatible with the local file store.");
        });

        await suite.RunAsync("File policies can be imported into Blob without changing PINs or versions", async () =>
        {
            var path = suite.FilePath("blob-import.json");
            var original = new AccessPolicy("POL-IMPORT", 17, true, "even") { AccessMode = "pinOnly", Pin = Credential };
            using (var local = new FilePolicyStore(path)) Require(await local.CreateAsync(original), "Local fixture creation failed.");
            var bytes = await File.ReadAllBytesAsync(path);
            foreach (var imported in new[] { bytes, Encoding.UTF8.GetPreamble().Concat(bytes).ToArray() })
            {
                using var service = new MemoryBlobService();
                service.Seed(imported);
                var before = service.StoredBytes!;
                Require((await service.OpenStore().GetAllAsync()).Single() == original, "Import with or without a UTF-8 BOM changed the schema, PIN verifier or version.");
                Require(service.WriteRequests == 0 && service.StoredBytes!.SequenceEqual(before), "Reading imported policies rewrote the document.");
            }
        });

        await suite.RunAsync("Blob legacy migration stays paused and persists schema 2 only after mutation", async () =>
        {
            const string legacy = "[{\"policyId\":\"POL-A\",\"version\":7,\"enabled\":true,\"allowedMinuteParity\":\"even\"},{\"policyId\":\"POL-B\",\"version\":1,\"enabled\":true,\"allowedMinuteParity\":\"odd\"}]";
            using var service = new MemoryBlobService();
            service.Seed(Encoding.UTF8.GetBytes(legacy));
            var store = service.OpenStore();
            var policies = await store.GetAllAsync();
            Require(policies.Count == 2 && policies.All(policy => !policy.Enabled && policy.SchemaVersion == 2 && policy.AccessMode == "fingerprintOnly" && policy.Pin is null), "Legacy activation or default schema was changed.");
            Require(policies.Single(policy => policy.PolicyId == "POL-A").Version == 7 && service.WriteRequests == 0, "Read migration changed versions or rewrote the blob.");
            Require(await store.CreateAsync(new AccessPolicy("POL-C", 1, false, "any")), "Mutation after legacy import failed.");
            using var stored = JsonDocument.Parse(service.StoredBytes!);
            Require(stored.RootElement.EnumerateArray().All(item => item.GetProperty("schemaVersion").GetInt32() == 2), "Mutation did not persist the migrated format.");
        });

        await suite.RunAsync("Blob simultaneous first writes use If-None-Match and retain both policies", async () =>
        {
            using var service = new MemoryBlobService();
            service.CollideNextTwoReads();
            var first = service.OpenStore();
            var second = service.OpenStore();
            var created = await Task.WhenAll(
                first.CreateAsync(new AccessPolicy("POL-FIRST", 1, true, "any")),
                second.CreateAsync(new AccessPolicy("POL-SECOND", 1, true, "any")))
                .WaitAsync(TimeSpan.FromSeconds(15));
            Require(created.All(result => result), "An initial-write collision rejected a distinct policy.");
            var policies = await first.GetAllAsync();
            Require(policies.Count == 2 && policies.Count(policy => policy.Enabled) == 1, "Initial-write collision lost data or enabled multiple policies.");
            Require(policies.All(policy => policy.Version == (policy.Enabled ? 1 : 2)), "The retried first-write mutation advanced the wrong versions.");
            Require(service.CreationConflicts == 1 && service.CreateConditions >= 2 && service.MatchConditions >= 1, "The initial race did not exercise conditional creation and an ETag retry.");
            Require(service.AllWritesConditional, "An unconditional write bypassed the initial race.");
        });

        await suite.RunAsync("Blob stale ETag retries reload state and preserve single activation", async () =>
        {
            using var service = new MemoryBlobService();
            var original = new AccessPolicy("POL-ORIGINAL", 1, true, "any");
            service.Seed(JsonSerializer.SerializeToUtf8Bytes(new[] { original }, JsonOptions));
            service.CollideNextTwoReads();
            var results = await Task.WhenAll(
                service.OpenStore().CreateAsync(new AccessPolicy("POL-B", 1, true, "even")),
                service.OpenStore().CreateAsync(new AccessPolicy("POL-C", 1, true, "odd")))
                .WaitAsync(TimeSpan.FromSeconds(15));
            Require(results.All(result => result), "A stale ETag rejected a distinct mutation.");
            var policies = await service.OpenStore().GetAllAsync();
            Require(policies.Count == 3 && policies.Count(policy => policy.Enabled) == 1, "An ETag retry lost another writer's policy or enabled multiple policies.");
            Require(policies.Single(policy => policy.PolicyId == original.PolicyId) == (original with { Enabled = false, Version = 2 }), "Reloading after a conflict repeated the original version increment.");
            Require(policies.Where(policy => policy.PolicyId != original.PolicyId).All(policy => policy.Version == (policy.Enabled ? 1 : 2)), "Retry did not recalculate versions from the winning document.");
            Require(service.EtagConflicts == 1 && service.AllWritesConditional, "The stale-read race did not exercise If-Match conflict handling.");
        });

        await suite.RunAsync("Blob corrupt or conflicting documents fail without overwrite", async () =>
        {
            var active = new AccessPolicy("POL-A", 1, true, "any");
            foreach (var document in new[]
            {
                "not JSON", "null", "{}", "[{}]",
                JsonSerializer.Serialize(new[] { active, active with { PolicyId = "POL-B" } }, JsonOptions),
                JsonSerializer.Serialize(new[] { active, active with { PolicyId = "pol-a", Enabled = false } }, JsonOptions)
            })
            {
                using var service = new MemoryBlobService();
                var bytes = Encoding.UTF8.GetBytes(document);
                service.Seed(bytes);
                var store = service.OpenStore();
                await ThrowsStoredDataAsync(() => store.GetAllAsync());
                await ThrowsStoredDataAsync(() => store.CreateAsync(new AccessPolicy("POL-NEW", 1, false, "any")));
                Require(service.WriteRequests == 0 && service.StoredBytes!.SequenceEqual(bytes), "Malformed data was replaced by an empty or new document.");
            }
        });

        await suite.RunAsync("Blob auth, missing-container and transport errors fail closed", async () =>
        {
            foreach (var fault in new[]
            {
                new BlobFault(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch"),
                new BlobFault(HttpStatusCode.NotFound, "ContainerNotFound"),
                new BlobFault(HttpStatusCode.ServiceUnavailable, "ServerBusy")
            })
            {
                using var service = new MemoryBlobService { ReadFault = fault };
                var store = service.OpenStore();
                await ThrowsAsync<IOException>(() => store.GetAllAsync());
                await ThrowsAsync<IOException>(() => store.CreateAsync(new AccessPolicy("POL-NEW", 1, false, "any")));
                Require(service.WriteRequests == 0, "A read failure was treated as a missing document.");
            }
            using var network = new MemoryBlobService { FailReadTransport = true };
            await ThrowsAsync<IOException>(() => network.OpenStore().GetAllAsync());
            Require(network.WriteRequests == 0, "A network failure unexpectedly wrote a policy.");
        });

        await suite.RunAsync("Blob non-conflict write failures do not retry or change data", async () =>
        {
            foreach (var fault in new[]
            {
                new BlobFault(HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch"),
                new BlobFault(HttpStatusCode.PreconditionFailed, "LeaseIdMissing")
            })
            {
                using var service = new MemoryBlobService { WriteFault = fault };
                service.Seed(Encoding.UTF8.GetBytes("[]"));
                await ThrowsAsync<IOException>(() => service.OpenStore().CreateAsync(new AccessPolicy("POL-NEW", 1, false, "any")));
                Require(service.WriteRequests == 1 && Encoding.UTF8.GetString(service.StoredBytes!) == "[]", "A non-ETag failure retried or changed stored data.");
            }
        });

        await suite.RunAsync("Blob repeated conflicts exhaust a bounded retry budget without overwrite", async () =>
        {
            using var service = new MemoryBlobService { AlwaysConflictWrites = true };
            service.Seed(Encoding.UTF8.GetBytes("[]"));
            await ThrowsAsync<IOException>(() => service.OpenStore(maxWriteAttempts: 3).CreateAsync(new AccessPolicy("POL-NEW", 1, true, "any")));
            Require(service.WriteRequests == 3 && Encoding.UTF8.GetString(service.StoredBytes!) == "[]", "Conflicts were unbounded or bypassed conditional writes.");
        });

        await suite.RunAsync("Blob cancellation propagates before and during reads without a write", async () =>
        {
            using var service = new MemoryBlobService();
            var store = service.OpenStore();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => store.GetAllAsync(cancelled.Token));
            await ThrowsAsync<OperationCanceledException>(() => store.CreateAsync(new AccessPolicy("POL-NEW", 1, false, "any"), cancelled.Token));
            await ThrowsAsync<OperationCanceledException>(() => store.SetActiveAsync(null, cancelled.Token));
            Require(service.WriteRequests == 0, "Pre-cancelled operations issued a write.");

            using var inFlight = new MemoryBlobService { HoldReads = true };
            using var cancellation = new CancellationTokenSource();
            var reading = inFlight.OpenStore().GetAllAsync(cancellation.Token);
            await inFlight.FirstRead.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
            Require(inFlight.WriteRequests == 0, "Cancellation during download issued a write.");
        });
    }

    private sealed record BlobFault(HttpStatusCode Status, string Code);

    // A tiny versioned Blob HTTP endpoint behind the REAL Azure SDK pipeline.
    // No cloud account, credentials, network calls or SDK client method mocks.
    private sealed class MemoryBlobService : HttpMessageHandler
    {
        private readonly object gate = new();
        private readonly List<HttpClient> clients = [];
        private byte[]? content;
        private int version;
        private ReadBarrier? barrier;
        private readonly TaskCompletionSource firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource heldRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlobFault? ReadFault { get; init; }
        public BlobFault? WriteFault { get; init; }
        public bool FailReadTransport { get; init; }
        public bool AlwaysConflictWrites { get; init; }
        public bool HoldReads { get; init; }
        public int WriteRequests { get; private set; }
        public int CreationConflicts { get; private set; }
        public int EtagConflicts { get; private set; }
        public int CreateConditions { get; private set; }
        public int MatchConditions { get; private set; }
        public bool AllWritesConditional { get; private set; } = true;
        public bool JsonContentTypesOnly { get; private set; } = true;
        public Task FirstRead => firstRead.Task;
        public byte[]? StoredBytes { get { lock (gate) return content?.ToArray(); } }

        public BlobPolicyStore OpenStore(int maxWriteAttempts = 8)
        {
            var http = new HttpClient(this, disposeHandler: false);
            lock (gate) clients.Add(http);
            var options = new BlobClientOptions { Transport = new HttpClientTransport(http) };
            options.Retry.MaxRetries = 0; // Only the store's conditional-write retry is under test.
            var client = new BlobClient(new Uri("https://policy-tests.blob.core.windows.net/policies/policies.json"), options);
            return new BlobPolicyStore(client, maxWriteAttempts);
        }

        public void Seed(byte[] bytes)
        {
            lock (gate) { content = bytes.ToArray(); ++version; }
        }

        public void CollideNextTwoReads()
        {
            lock (gate) barrier = new ReadBarrier();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(request.RequestUri?.AbsolutePath == "/policies/policies.json", "Unexpected blob/container HTTP operation.");
            if (request.Method == HttpMethod.Get)
            {
                firstRead.TrySetResult();
                if (FailReadTransport) throw new HttpRequestException("Simulated network outage.");
                if (ReadFault is not null) return Error(ReadFault);
                byte[]? snapshot;
                string etag;
                Task? wait = null;
                lock (gate)
                {
                    snapshot = content?.ToArray();
                    etag = Etag(version);
                    if (barrier is not null)
                    {
                        wait = barrier.Ready.Task;
                        if (--barrier.Remaining == 0) { barrier.Ready.TrySetResult(); barrier = null; }
                    }
                }
                if (wait is not null) await wait.WaitAsync(cancellationToken);
                if (HoldReads) await heldRead.Task.WaitAsync(cancellationToken);
                if (snapshot is null) return Error(new(HttpStatusCode.NotFound, "BlobNotFound"));
                return Download(request, snapshot, etag);
            }
            if (request.Method == HttpMethod.Put)
            {
                byte[] uploaded = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate)
                {
                    ++WriteRequests;
                    var match = Header(request, "If-Match");
                    var none = Header(request, "If-None-Match");
                    var create = none == "*" && match is null;
                    var update = match is not null && none is null;
                    AllWritesConditional &= create || update;
                    var blobContentType = Header(request, "x-ms-blob-content-type") ?? request.Content?.Headers.ContentType?.MediaType;
                    JsonContentTypesOnly &= blobContentType == "application/json";
                    if (create) ++CreateConditions;
                    if (update) ++MatchConditions;
                    if (WriteFault is not null) return Error(WriteFault);
                    if (AlwaysConflictWrites) return Error(new(HttpStatusCode.PreconditionFailed, "ConditionNotMet"));
                    if (create && content is not null)
                    {
                        ++CreationConflicts;
                        return Error(new(HttpStatusCode.Conflict, "BlobAlreadyExists"));
                    }
                    if (update && (content is null || match != Etag(version)))
                    {
                        ++EtagConflicts;
                        return Error(new(HttpStatusCode.PreconditionFailed, "ConditionNotMet"));
                    }
                    if (!create && !update) return Error(new(HttpStatusCode.BadRequest, "InvalidHeaderValue"));
                    content = uploaded;
                    ++version;
                    return Response(HttpStatusCode.Created, [], Etag(version));
                }
            }
            throw new InvalidOperationException($"Unexpected HTTP method {request.Method}.");
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

        private static string Etag(int value) => $"\"version-{value}\"";

        private static HttpResponseMessage Download(HttpRequestMessage request, byte[] snapshot, string etag)
        {
            var range = Header(request, "x-ms-range") ?? Header(request, "Range");
            if (range is null) return Response(HttpStatusCode.OK, snapshot, etag);
            var parts = range["bytes=".Length..].Split('-');
            var start = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var end = parts[1].Length == 0 ? snapshot.Length - 1 : Math.Min(long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), snapshot.Length - 1);
            var response = Response(HttpStatusCode.PartialContent, snapshot[start..((int)end + 1)], etag);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, snapshot.Length);
            return response;
        }

        private static HttpResponseMessage Response(HttpStatusCode status, byte[] bytes, string? etag = null)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
            response.Headers.TryAddWithoutValidation("x-ms-request-id", Guid.NewGuid().ToString());
            response.Headers.TryAddWithoutValidation("x-ms-version", "2023-11-03");
            response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            response.Headers.TryAddWithoutValidation("x-ms-server-encrypted", "true");
            response.Headers.AcceptRanges.Add("bytes");
            if (etag is not null) response.Headers.ETag = new EntityTagHeaderValue(etag);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            response.Content.Headers.ContentLength = bytes.Length;
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
            return response;
        }

        private static HttpResponseMessage Error(BlobFault error)
        {
            var response = Response(error.Status, Encoding.UTF8.GetBytes($"<Error><Code>{error.Code}</Code><Message>Simulated storage failure.</Message></Error>"));
            response.Headers.TryAddWithoutValidation("x-ms-error-code", error.Code);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (var client in clients) client.Dispose();
            base.Dispose(disposing);
        }

        private sealed class ReadBarrier
        {
            public int Remaining = 2;
            public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
