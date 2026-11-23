using System.Text.Json;
using PolicyApi;
using static CheckSuite;

var suite = new CheckSuite();
try
{
    await PolicyStoreChecks.RunAsync(suite);
    await BlobPolicyStoreChecks.RunAsync(suite);
    await suite.RunAsync("Removal preserves survivors, cancellation and persisted activation", async () =>
    {
        var path = suite.FilePath("removal/policies.json");
        var active = new AccessPolicy("POL-ACTIVE", 7, true, "even");
        var inactive = new AccessPolicy("POL-INACTIVE", 3, false, "odd");
        var survivor = new AccessPolicy("POL-SURVIVOR", 11, false, "any")
        {
            AccessMode = "denyAll", StartMinute = 360, EndMinute = 1080
        };
        using (var store = new FilePolicyStore(path))
        {
            Require(!await store.RemoveAsync("MISSING"), "Removing an unknown ID from a new store should return false.");
            Require(!File.Exists(path), "Removing an unknown ID created a policy file.");
            await store.CreateAsync(active);
            await store.CreateAsync(inactive);
            await store.CreateAsync(survivor);

            Require(await store.RemoveAsync("pol-inactive"), "Removal should match IDs case-insensitively.");
            Require((await store.GetAllAsync()).SequenceEqual(new[] { active, survivor }), "Removing an inactive policy changed the active policy or another survivor.");

            var beforeCancellation = await File.ReadAllTextAsync(path);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => store.RemoveAsync(active.PolicyId, cancellation.Token));
            Require(await File.ReadAllTextAsync(path) == beforeCancellation, "Cancelled removal changed the policy file.");
            Require((await store.GetAllAsync()).SequenceEqual(new[] { active, survivor }), "Cancelled removal changed stored policies or left the store unusable.");

            Require(await store.RemoveAsync(active.PolicyId), "Removing the active policy failed after cancellation.");
            Require((await store.GetAllAsync()).SequenceEqual(new[] { survivor }), "Removing the active policy activated or changed a surviving policy.");

            var beforeMissing = await File.ReadAllTextAsync(path);
            File.SetLastWriteTimeUtc(path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var lastWrite = File.GetLastWriteTimeUtc(path);
            Require(!await store.RemoveAsync(active.PolicyId), "Removing an already removed ID should return false.");
            Require(await File.ReadAllTextAsync(path) == beforeMissing && File.GetLastWriteTimeUtc(path) == lastWrite, "Removing an unknown ID rewrote the policy file.");
        }

        using var reopened = new FilePolicyStore(path);
        Require((await reopened.GetAllAsync()).SequenceEqual(new[] { survivor }), "Removals or survivor state changed after restarting the store.");
    });
    await PinProtectionChecks.RunAsync(suite);
}
finally
{
    suite.Cleanup();
}

Console.WriteLine($"{suite.Passed} passed; {suite.Failed} failed.");
return suite.Failed == 0 ? 0 : 1;

internal sealed class CheckSuite
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"uno-policy-tests-{Guid.NewGuid():N}");
    public int Passed { get; private set; }
    public int Failed { get; private set; }

    public string FilePath(string name)
    {
        Directory.CreateDirectory(testRoot);
        return Path.Combine(testRoot, name);
    }

    public async Task RunAsync(string name, Func<Task> check)
    {
        try
        {
            await check();
            Passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            Failed++;
            Console.Error.WriteLine($"FAIL {name}: {exception}");
        }
    }

    public void Cleanup()
    {
        // Delete only this process's uniquely named directory, directly beneath the OS temp directory.
        var resolved = Path.GetFullPath(testRoot);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(resolved) && Directory.GetParent(resolved)?.FullName == parent &&
            Path.GetFileName(resolved).StartsWith("uno-policy-tests-", StringComparison.Ordinal))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    public static async Task ThrowsStoredDataAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException) { return; }
        throw new InvalidOperationException("Expected an invalid stored-data error.");
    }
}
