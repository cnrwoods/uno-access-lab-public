namespace PolicyApi;

// Shared policy rules; each backend supplies a consistent snapshot and an
// atomic read/modify/write operation. A backend may repeat a mutation after a
// concurrency conflict, so delegates must operate only on the supplied snapshot.
public abstract class JsonPolicyStore : IPolicyStore
{
    public async Task<IReadOnlyList<AccessPolicy>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await ReadPoliciesAsync(cancellationToken);

    public async Task<bool> CreateAsync(AccessPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        PolicyDocument.ValidatePolicy(policy);

        return await UpdatePoliciesAsync(policies =>
        {
            if (policies.Any(existing => SameId(existing.PolicyId, policy.PolicyId)))
            {
                return new PolicyUpdate(policies, Result: false, Changed: false);
            }

            var updatedPolicies = policies
                .Select(existing => policy.Enabled ? SetEnabled(existing, false) : existing)
                .Append(policy)
                .OrderBy(item => item.PolicyId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new PolicyUpdate(updatedPolicies, Result: true, Changed: true);
        }, cancellationToken);
    }

    public async Task<bool> RemoveAsync(string policyId, CancellationToken cancellationToken = default) =>
        await UpdatePoliciesAsync(policies =>
        {
            var remaining = policies.Where(policy => !SameId(policy.PolicyId, policyId)).ToArray();
            var changed = remaining.Length != policies.Length;
            return new PolicyUpdate(remaining, Result: changed, Changed: changed);
        }, cancellationToken);

    public async Task<bool> SetActiveAsync(string? policyId, CancellationToken cancellationToken = default) =>
        await UpdatePoliciesAsync(policies =>
        {
            if (policyId is not null && !policies.Any(policy => SameId(policy.PolicyId, policyId)))
            {
                return new PolicyUpdate(policies, Result: false, Changed: false);
            }

            var updatedPolicies = policies
                .Select(policy => SetEnabled(policy, policyId is not null && SameId(policy.PolicyId, policyId)))
                .ToArray();
            return new PolicyUpdate(updatedPolicies, Result: true,
                Changed: !policies.SequenceEqual(updatedPolicies));
        }, cancellationToken);

    public async Task<bool> UpdatePinAsync(string policyId, PinCredential pin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pin);
        PolicyDocument.ValidatePin(pin);

        return await UpdatePoliciesAsync(policies =>
        {
            var index = Array.FindIndex(policies, policy => SameId(policy.PolicyId, policyId));
            if (index < 0)
            {
                return new PolicyUpdate(policies, Result: false, Changed: false);
            }

            var policy = policies[index];
            if (!UsesPin(policy.AccessMode))
            {
                throw new ArgumentException("This policy does not use a PIN.", nameof(policyId));
            }

            // Do not mutate a snapshot that a backend or caller may retain.
            var updatedPolicies = (AccessPolicy[])policies.Clone();
            updatedPolicies[index] = policy with { Pin = pin, Version = NextVersion(policy) };
            return new PolicyUpdate(updatedPolicies, Result: true, Changed: true);
        }, cancellationToken);
    }

    protected abstract Task<AccessPolicy[]> ReadPoliciesAsync(CancellationToken cancellationToken);

    protected abstract Task<bool> UpdatePoliciesAsync(
        Func<AccessPolicy[], PolicyUpdate> update, CancellationToken cancellationToken);

    protected readonly record struct PolicyUpdate(AccessPolicy[] Policies, bool Result, bool Changed);

    private static bool SameId(string left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool UsesPin(string accessMode) => accessMode is "fingerprintOrPin" or "pinOnly";

    private static AccessPolicy SetEnabled(AccessPolicy policy, bool enabled) =>
        policy.Enabled == enabled ? policy : policy with { Enabled = enabled, Version = NextVersion(policy) };

    private static int NextVersion(AccessPolicy policy) => policy.Version < int.MaxValue
        ? policy.Version + 1
        : throw new InvalidDataException("The policy version cannot be incremented.");
}
