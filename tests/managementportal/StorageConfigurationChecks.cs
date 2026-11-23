using System.Net;
using ManagementPortal.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PolicyApi;
using static ManagementPortal.Tests.TestProgram;

namespace ManagementPortal.Tests;

internal static class StorageConfigurationChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Policy storage defaults to File and accepts an explicit File provider", () =>
        {
            foreach (var providerName in new string?[] { null, "File", "file" })
            {
                var settings = new Dictionary<string, string?>
                {
                    ["PolicyStore:FilePath"] = "isolated/policies.json"
                };
                if (providerName is not null) settings["PolicyStore:Provider"] = providerName;
                using var services = BuildServices(settings);
                ValidateStartup(services);
                Require(services.GetRequiredService<IPolicyStore>() is FilePolicyStore,
                    "An omitted or explicit File provider did not preserve local storage.");
            }
            return Task.CompletedTask;
        });

        await check("Blob configuration selects Blob without processing FilePath or contacting Azure", () =>
        {
            foreach (var environment in new[] { Environments.Development, Environments.Production })
            {
                var settings = ValidBlob();
                settings["PolicyStore:FilePath"] = "irrelevant\0file-path";
                using var services = BuildServices(settings, environment);
                ValidateStartup(services);
                Require(services.GetRequiredService<IPolicyStore>() is BlobPolicyStore,
                    "AzureBlob did not select the remote store independently of FilePath.");
            }
            return Task.CompletedTask;
        });

        await check("Unknown, incomplete and unsafe Blob configuration fail startup validation", () =>
        {
            var invalid = new List<Dictionary<string, string?>>
            {
                new() { ["PolicyStore:Provider"] = "unknown" },
                new() { ["PolicyStore:Provider"] = "" },
                new() { ["PolicyStore:Provider"] = "AzureBlob" }
            };
            foreach (var endpoint in new[]
            {
                "http://storage.example.test", "not-an-absolute-uri",
                "https://storage.example.test/container", "https://storage.example.test/?sig=synthetic",
                "https://storage.example.test/#fragment", "https://user:synthetic@storage.example.test"
            })
            {
                var settings = ValidBlob();
                settings["PolicyStore:BlobServiceUri"] = endpoint;
                invalid.Add(settings);
            }
            foreach (var container in new string?[] { null, "ab", "UPPERCASE", "bad--name", "-policies", "policies-" })
            {
                var settings = ValidBlob();
                settings["PolicyStore:ContainerName"] = container;
                invalid.Add(settings);
            }
            foreach (var blob in new string?[] { null, "", " ", "control\0name", new('a', 1025) })
            {
                var settings = ValidBlob();
                settings["PolicyStore:BlobName"] = blob;
                invalid.Add(settings);
            }
            foreach (var settings in invalid)
            {
                using var services = BuildServices(settings);
                try { ValidateStartup(services); }
                catch (OptionsValidationException) { continue; }
                throw new InvalidOperationException("Invalid storage configuration silently passed startup validation.");
            }
            return Task.CompletedTask;
        });

        await check("PIN key-file lookup preserves File mode and is independent of Blob settings", () =>
        {
            var environment = new TestEnvironment();
            var local = Configuration(new Dictionary<string, string?> { ["PolicyStore:FilePath"] = "custom/policies.json" });
            Require(PolicyStoreConfiguration.GetSharedKeyFilePath(local, environment) ==
                    Path.Combine(environment.ContentRootPath, "custom", "device-shared-key.hex"),
                "File mode changed the existing key beside the configured policy file.");

            var settings = ValidBlob();
            settings["PolicyStore:FilePath"] = "irrelevant\0file-path";
            Require(PolicyStoreConfiguration.GetSharedKeyFilePath(Configuration(settings), environment) ==
                    Path.Combine(environment.ContentRootPath, "App_Data", "device-shared-key.hex"),
                "Blob mode coupled the local key to a Blob URI or FilePath.");
            settings["PolicySecurity:SharedKeyFile"] = "keys/registration.hex";
            Require(PolicyStoreConfiguration.GetSharedKeyFilePath(Configuration(settings), environment) ==
                    Path.Combine(environment.ContentRootPath, "keys", "registration.hex"),
                "An explicit key-file setting was not respected.");
            return Task.CompletedTask;
        });

        await check("Every device API read returns 503/no-store on storage failure without leaking details", async () =>
        {
            using var factory = new PortalFactory { StoreOverride = new UnavailableStore() };
            using var browser = factory.Browser();
            foreach (var path in new[] { "/api/policies", "/api/policy", "/api/policy/active", "/api/policies/ANY" })
            {
                var response = await browser.GetAsync(path);
                Require(response.StatusCode == HttpStatusCode.ServiceUnavailable,
                    $"{path} did not fail closed with HTTP 503.");
                Require(response.Headers.CacheControl?.NoStore == true,
                    "A storage failure response could be cached.");
                Require(!(await response.Content.ReadAsStringAsync()).Contains("private-storage-probe", StringComparison.Ordinal),
                    "The device response exposed internal storage failure details.");
            }
        });
    }

    private static Dictionary<string, string?> ValidBlob() => new()
    {
        ["PolicyStore:Provider"] = "AzureBlob",
        ["PolicyStore:BlobServiceUri"] = "https://storage.example.test",
        ["PolicyStore:ContainerName"] = "policies",
        ["PolicyStore:BlobName"] = "policies.json"
    };

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings,
        string environment = "Development")
    {
        var services = new ServiceCollection();
        services.AddSingleton(Configuration(settings));
        services.AddSingleton<IHostEnvironment>(new TestEnvironment { EnvironmentName = environment });
        services.AddPolicyStorage();
        return services.BuildServiceProvider();
    }

    private static void ValidateStartup(ServiceProvider services) =>
        services.GetRequiredService<IStartupValidator>().Validate();

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "PolicyStorageConfigurationChecks";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "uno-policy-config-checks");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class UnavailableStore : IPolicyStore
    {
        public Task<IReadOnlyList<AccessPolicy>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<AccessPolicy>>(new IOException("private-storage-probe"));
        public Task<bool> CreateAsync(AccessPolicy policy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(string policyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetActiveAsync(string? policyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdatePinAsync(string policyId, PinCredential pin, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
