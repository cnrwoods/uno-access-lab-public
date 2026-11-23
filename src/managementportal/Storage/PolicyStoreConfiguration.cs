using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using PolicyApi;

namespace ManagementPortal.Storage;

public sealed class PolicyStoreOptions
{
    public string Provider { get; set; } = "File";
    public string? FilePath { get; set; }
    public string? BlobServiceUri { get; set; }
    public string? ContainerName { get; set; }
    public string? BlobName { get; set; }
}

public static class PolicyStoreConfiguration
{
    public static IServiceCollection AddPolicyStorage(this IServiceCollection services)
    {
        services.AddOptions<PolicyStoreOptions>()
            .BindConfiguration("PolicyStore")
            .Validate(options => IsFile(options.Provider) || IsBlob(options.Provider),
                "PolicyStore:Provider must be File or AzureBlob.")
            .Validate(options => !IsBlob(options.Provider) || ValidServiceUri(options.BlobServiceUri),
                "PolicyStore:BlobServiceUri must be an HTTPS service root without credentials, query or fragment.")
            .Validate(options => !IsBlob(options.Provider) || ValidContainerName(options.ContainerName),
                "PolicyStore:ContainerName must be a valid lowercase Azure container name.")
            .Validate(options => !IsBlob(options.Provider) ||
                (!string.IsNullOrWhiteSpace(options.BlobName) && options.BlobName.Length <= 1024 &&
                 !options.BlobName.Any(char.IsControl)),
                "PolicyStore:BlobName is required and must be a valid blob name.")
            .ValidateOnStart();

        services.AddSingleton<IPolicyStore>(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var environment = provider.GetRequiredService<IHostEnvironment>();
            var options = provider.GetRequiredService<IOptions<PolicyStoreOptions>>().Value;
            if (IsFile(options.Provider))
                return new FilePolicyStore(GetFilePath(configuration, environment));

            TokenCredential credential = environment.IsDevelopment()
                ? new DefaultAzureCredential(new DefaultAzureCredentialOptions
                    { ExcludeInteractiveBrowserCredential = true })
                : new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned);
            var clientOptions = new BlobClientOptions();
            // The store itself retries ETag conflicts from fresh snapshots. Do
            // not replay a conditional write after an ambiguous network failure.
            clientOptions.Retry.MaxRetries = 0;
            clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
            var service = new BlobServiceClient(new Uri(options.BlobServiceUri!), credential, clientOptions);
            var blob = service.GetBlobContainerClient(options.ContainerName!).GetBlobClient(options.BlobName!);
            return new BlobPolicyStore(blob);
        });
        return services;
    }

    public static string GetFilePath(IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredPath = configuration["PolicyStore:FilePath"];
        var defaultPath = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"))
            ? Path.Combine(environment.ContentRootPath, "App_Data", "policies.json")
            : Path.Combine(Path.GetTempPath(), "uno-access-lab", "policies.json");
        return string.IsNullOrWhiteSpace(configuredPath)
            ? defaultPath
            : Path.GetFullPath(configuredPath, environment.ContentRootPath);
    }

    public static string GetSharedKeyFilePath(IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredKeyFile = configuration["PolicySecurity:SharedKeyFile"];
        if (!string.IsNullOrWhiteSpace(configuredKeyFile))
            return Path.GetFullPath(configuredKeyFile, environment.ContentRootPath);

        // Preserve the bench file-provider fallback, without ever deriving a
        // local key path from a Blob URI or irrelevant FilePath in AzureBlob mode.
        var provider = configuration["PolicyStore:Provider"] ?? "File";
        var directory = IsFile(provider)
            ? Path.GetDirectoryName(GetFilePath(configuration, environment))!
            : Path.Combine(environment.ContentRootPath, "App_Data");
        return Path.Combine(directory, "device-shared-key.hex");
    }

    private static bool IsFile(string provider) =>
        string.Equals(provider, "File", StringComparison.OrdinalIgnoreCase);

    private static bool IsBlob(string provider) =>
        string.Equals(provider, "AzureBlob", StringComparison.OrdinalIgnoreCase);

    private static bool ValidServiceUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath == "/";

    private static bool ValidContainerName(string? name)
    {
        if (name is null || name.Length is < 3 or > 63 || name[0] == '-' || name[^1] == '-') return false;
        var previousDash = false;
        foreach (var character in name)
        {
            if (character == '-')
            {
                if (previousDash) return false;
                previousDash = true;
            }
            else
            {
                if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')) return false;
                previousDash = false;
            }
        }
        return true;
    }
}
