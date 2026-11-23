namespace PolicyApi;

// Register one singleton per file. This POC coordinates requests within one
// process only; it is not a store for multiple application instances.
public sealed class FilePolicyStore(string path) : JsonPolicyStore, IDisposable
{
    private readonly string filePath = Path.GetFullPath(path);
    private readonly SemaphoreSlim gate = new(1, 1);

    protected override async Task<AccessPolicy[]> ReadPoliciesAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    protected override async Task<bool> UpdatePoliciesAsync(
        Func<AccessPolicy[], PolicyUpdate> update, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var policies = await ReadCoreAsync(cancellationToken);
            var result = update(policies);
            if (result.Changed)
            {
                await WritePoliciesAsync(result.Policies, cancellationToken);
            }
            return result.Result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AccessPolicy[]> ReadCoreAsync(CancellationToken cancellationToken)
    {
        byte[] json;
        try
        {
            json = await File.ReadAllBytesAsync(filePath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        return PolicyDocument.Parse(json);
    }

    private async Task WritePoliciesAsync(AccessPolicy[] policies, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = PolicyDocument.Serialize(policies);
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await stream.WriteAsync(json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // The completed temporary file replaces the destination on the same
            // filesystem. A failed write leaves the existing policy file intact.
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Cleanup must not hide the original storage failure.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup must not hide the original storage failure.
            }
        }
    }

    public void Dispose() => gate.Dispose();
}
