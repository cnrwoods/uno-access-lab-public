using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace PolicyApi;

// Keep the complete policy list in one private blob so activation is atomic.
// Each conditional write is based on a freshly read ETag, including on retries.
public sealed class BlobPolicyStore : JsonPolicyStore
{
    private readonly BlobClient blobClient;
    private readonly int maxWriteAttempts;

    public BlobPolicyStore(BlobClient blobClient, int maxWriteAttempts = 8)
    {
        // Configure the supplied client's SDK retry count to zero, as the portal
        // factory does. A lost upload response is not safe to replay as a new mutation.
        ArgumentNullException.ThrowIfNull(blobClient);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWriteAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxWriteAttempts, 32);
        this.blobClient = blobClient;
        this.maxWriteAttempts = maxWriteAttempts;
    }

    protected override async Task<AccessPolicy[]> ReadPoliciesAsync(CancellationToken cancellationToken) =>
        (await ReadSnapshotAsync(cancellationToken)).Policies;

    protected override async Task<bool> UpdatePoliciesAsync(
        Func<AccessPolicy[], PolicyUpdate> update, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < maxWriteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await ReadSnapshotAsync(cancellationToken);
            var change = update(snapshot.Policies);
            cancellationToken.ThrowIfCancellationRequested();
            if (!change.Changed) return change.Result;

            var options = new BlobUploadOptions
            {
                Conditions = snapshot.ETag is { } etag
                    ? new BlobRequestConditions { IfMatch = etag }
                    : new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = "application/json",
                    CacheControl = "no-store"
                }
            };

            try
            {
                await blobClient.UploadAsync(new BinaryData(PolicyDocument.Serialize(change.Policies)),
                    options, cancellationToken);
                return change.Result;
            }
            catch (RequestFailedException exception) when (IsWriteConflict(exception, snapshot.ETag))
            {
                if (attempt + 1 == maxWriteAttempts)
                    throw new IOException("Policy storage changed too often. Please retry the operation.", exception);
                // Recompute the mutation from the latest document; never retry a stale payload.
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1) + Random.Shared.Next(5, 20)),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRemoteFailure(exception))
            {
                throw StorageUnavailable(exception);
            }
        }
        throw new IOException("Policy storage could not be updated.");
    }

    private async Task<Snapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var download = (await blobClient.DownloadContentAsync(cancellationToken)).Value;
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(download.Details.ETag.ToString()))
                throw new IOException("Policy storage returned no version identifier.");
            return new Snapshot(PolicyDocument.Parse(download.Content.ToMemory()), download.Details.ETag);
        }
        catch (RequestFailedException exception) when (
            exception.Status == 404 && exception.ErrorCode == BlobErrorCode.BlobNotFound.ToString())
        {
            // A new document is empty. Missing containers, denied access and other
            // failures are not empty stores and must never trigger local fallback.
            return new Snapshot([], null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRemoteFailure(exception))
        {
            throw StorageUnavailable(exception);
        }
    }

    private static bool IsWriteConflict(RequestFailedException exception, ETag? originalETag) =>
        (exception.Status == 412 && exception.ErrorCode == BlobErrorCode.ConditionNotMet.ToString()) ||
        (originalETag is null && exception.Status == 409 &&
            exception.ErrorCode == BlobErrorCode.BlobAlreadyExists.ToString());

    private static bool IsRemoteFailure(Exception exception) =>
        exception is RequestFailedException or AuthenticationFailedException or
            HttpRequestException or TimeoutException or OperationCanceledException;

    private static IOException StorageUnavailable(Exception exception) =>
        new("Azure policy storage is unavailable. Please try again later.", exception);

    private sealed record Snapshot(AccessPolicy[] Policies, ETag? ETag);
}
