namespace PolicyApi;

public interface IPolicyStore
{
    Task<IReadOnlyList<AccessPolicy>> GetAllAsync(CancellationToken cancellationToken = default);

    // Returns false when a policy with the same ID already exists (case-insensitive).
    Task<bool> CreateAsync(AccessPolicy policy, CancellationToken cancellationToken = default);

    // Unknown IDs return false. Removing the active policy leaves access paused.
    Task<bool> RemoveAsync(string policyId, CancellationToken cancellationToken = default);

    // A null ID pauses all policies. Unknown IDs return false.
    Task<bool> SetActiveAsync(string? policyId, CancellationToken cancellationToken = default);

    // The policy must already use a PIN mode. Unknown IDs return false.
    Task<bool> UpdatePinAsync(string policyId, PinCredential pin, CancellationToken cancellationToken = default);
}
