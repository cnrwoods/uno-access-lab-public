using System.Diagnostics;
using System.Net;
using ManagementPortal.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PolicyApi;
using static ManagementPortal.Tests.TestProgram;

namespace ManagementPortal.Tests;

internal static class HostingChecks
{
    private const string PublicHostname = "fingauth.conorwoods.co.uk";
    private const string FrontDoorId = "11111111-2222-3333-4444-555555555555";
    private static readonly IPAddress TrustedProxy = IPAddress.Parse("10.9.8.7");

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Hosted forwarding is disabled by default and cloud trust is an explicit validated mode", () =>
        {
            using (var services = HostingServices(new Dictionary<string, string?>()))
            {
                services.GetRequiredService<IStartupValidator>().Validate();
                Require(!services.GetRequiredService<IOptions<HostedIngressOptions>>().Value.Enabled,
                    "Local operation unexpectedly enabled hosted ingress.");
                Require(services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value.ForwardedHeaders == ForwardedHeaders.None,
                    "Local operation unexpectedly enabled forwarded headers.");
            }
            var settings = HostedSettings();
            settings.Remove("Hosting:KnownProxies:0");
            settings["Hosting:TrustAppServiceProxy"] = "true";
            settings["WEBSITE_INSTANCE_ID"] = "test-app-service-instance";
            using (var services = HostingServices(settings))
            {
                services.GetRequiredService<IStartupValidator>().Validate();
                var forwarding = services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
                Require(forwarding.KnownProxies.Count == 0 && forwarding.KnownIPNetworks.Count == 0,
                    "Explicit cloud proxy trust did not apply.");
                Require(forwarding.ForwardLimit == 2 && forwarding.AllowedHosts.SequenceEqual(new[] { PublicHostname }),
                    "Cloud forwarding lost its depth or public-host restriction.");
            }
            return Task.CompletedTask;
        });

        await check("Hosted ingress rejects unsafe startup configuration", () =>
        {
            var cases = new List<Dictionary<string, string?>>();
            foreach (var pair in new[]
            {
                ("Hosting:PublicHostname", "https://fingauth.conorwoods.co.uk"),
                ("Hosting:PublicHostname", "*.conorwoods.co.uk"),
                ("Hosting:FrontDoorId", "not-a-guid"),
                ("Hosting:KnownProxies:0", "not-an-ip"),
                ("Hosting:KnownIPNetworks:0", "not-a-network"),
                ("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true")
            })
            {
                var settings = HostedSettings();
                settings[pair.Item1] = pair.Item2;
                cases.Add(settings);
            }
            var withoutPlatform = HostedSettings();
            withoutPlatform.Remove("Hosting:KnownProxies:0");
            withoutPlatform["Hosting:TrustAppServiceProxy"] = "true";
            cases.Add(withoutPlatform);
            var disabledCloudTrust = new Dictionary<string, string?>
            {
                ["Hosting:TrustAppServiceProxy"] = "true",
                ["WEBSITE_INSTANCE_ID"] = "test-app-service-instance",
                ["Hosting:FrontDoorId"] = FrontDoorId
            };
            cases.Add(disabledCloudTrust);
            foreach (var settings in cases)
            {
                using var services = HostingServices(settings);
                try { services.GetRequiredService<IStartupValidator>().Validate(); }
                catch (OptionsValidationException) { continue; }
                throw new InvalidOperationException("Unsafe hosted proxy settings passed startup validation.");
            }
            return Task.CompletedTask;
        });

        await check("Health liveness is dependency-free and readiness failures are anonymous no-store 503", async () =>
        {
            var store = new UnhealthyStore();
            using var factory = HostedFactory(store);
            using var browser = factory.Browser("http://internal-origin.example.test");
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
            {
                var live = await browser.SendAsync(new HttpRequestMessage(method, "/health/live"));
                Require(live.StatusCode == HttpStatusCode.OK && live.Headers.Location is null &&
                    live.Headers.CacheControl?.NoStore == true, "Liveness redirected, challenged or cached.");
                Require((await live.Content.ReadAsByteArrayAsync()).Length == 0, "Liveness exposed a response body.");
            }
            Require(store.Reads == 0, "Liveness touched policy storage.");
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
            {
                var ready = await browser.SendAsync(new HttpRequestMessage(method, "/health/ready"));
                Require(ready.StatusCode == HttpStatusCode.ServiceUnavailable && ready.Headers.Location is null &&
                    ready.Headers.CacheControl?.NoStore == true, "Readiness did not return an uncached anonymous failure.");
                Require((await ready.Content.ReadAsByteArrayAsync()).Length == 0, "Readiness exposed dependency details.");
            }
            Require(store.Reads == 2, "Readiness did not check storage for both methods.");
        });

        await check("An accessible empty policy store is ready for GET and HEAD without authentication", async () =>
        {
            using var factory = HostedFactory();
            using var browser = factory.Browser("http://internal-origin.example.test");
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
            {
                var response = await browser.SendAsync(new HttpRequestMessage(method, "/health/ready"));
                Require(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
                    "An empty accessible store was not ready.");
            }
        });

        await check("Readiness has a bounded deadline even when a dependency ignores cancellation", async () =>
        {
            var store = new UnhealthyStore { NeverCompletes = true };
            using var factory = HostedFactory(store);
            using var browser = factory.Browser("http://internal-origin.example.test");
            var timer = Stopwatch.StartNew();
            var response = await browser.GetAsync("/health/ready").WaitAsync(TimeSpan.FromSeconds(8));
            Require(response.StatusCode == HttpStatusCode.ServiceUnavailable && timer.Elapsed < TimeSpan.FromSeconds(6),
                "A stuck dependency escaped the readiness deadline.");
            Require(store.LastCancellation.IsCancellationRequested, "Readiness did not cancel its dependency request.");
        });

        await check("Only trusted peers can restore the public HTTPS request from forwarded headers", async () =>
        {
            using (var factory = HostedFactory())
            using (var browser = factory.Browser("http://internal-origin.example.test"))
            {
                AddFrontDoorHeaders(browser);
                var response = await browser.GetAsync("/login");
                Require(response.StatusCode == HttpStatusCode.OK && response.Headers.Location is null,
                    "Trusted proxy forwarding did not restore HTTPS/public host.");
            }
            using (var factory = HostedFactory(peer: IPAddress.Parse("203.0.113.9")))
            using (var browser = factory.Browser("http://internal-origin.example.test"))
            {
                AddFrontDoorHeaders(browser);
                Require((await browser.GetAsync("/login")).StatusCode == HttpStatusCode.Forbidden,
                    "An untrusted peer could inject a forwarded public origin.");
            }
        });

        await check("Hosted ingress rejects missing, wrong or repeated Front Door IDs and spoofed hosts", async () =>
        {
            using var factory = HostedFactory();
            foreach (var identifier in new string?[] { null, "00000000-0000-0000-0000-000000000000", FrontDoorId + "," + FrontDoorId })
            {
                using var browser = factory.Browser("https://" + PublicHostname);
                if (identifier is not null) browser.DefaultRequestHeaders.TryAddWithoutValidation("X-Azure-FDID", identifier);
                var response = await browser.GetAsync("/login");
                Require(response.StatusCode == HttpStatusCode.Forbidden && response.Headers.CacheControl?.NoStore == true,
                    "The Front Door profile gate accepted an invalid identifier.");
            }
            foreach (var host in new[] { "attacker.example.test", PublicHostname + ":444" })
            {
                using var browser = factory.Browser("https://internal-origin.example.test");
                AddFrontDoorHeaders(browser, host: host);
                Require((await browser.GetAsync("/login")).StatusCode == HttpStatusCode.Forbidden,
                    "A forged host or noncanonical port was accepted.");
            }
        });

        await check("Hosted OIDC callbacks use the canonical public HTTPS hostname", async () =>
        {
            using var factory = HostedFactory();
            using var browser = factory.Browser("https://internal-origin.example.test");
            AddFrontDoorHeaders(browser, host: PublicHostname + ":443");
            var antiforgery = await Antiforgery(browser, "/login");
            var response = await browser.PostAsync("/login", Form(("__RequestVerificationToken", antiforgery)));
            Require(response.StatusCode == HttpStatusCode.Redirect, "Hosted sign-in did not start the OIDC flow.");
            var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
            Require(query["redirect_uri"] == "https://" + PublicHostname + "/signin-oidc",
                "Hosted OIDC used an origin/default hostname or incorrect scheme/port.");
        });

        await check("Hosted production upgrades HTTP device API requests to the public HTTPS URL", async () =>
        {
            using var factory = HostedFactory();
            using var browser = factory.Browser("http://internal-origin.example.test");
            AddFrontDoorHeaders(browser, scheme: "http");
            var response = await browser.GetAsync("/api/policy/active");
            Require(response.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect &&
                response.Headers.Location?.AbsoluteUri == "https://" + PublicHostname + "/api/policy/active",
                "The hosted device API accepted HTTP or redirected to the origin hostname.");
        });
    }

    private static Dictionary<string, string?> HostedSettings() => new()
    {
        ["Hosting:Enabled"] = "true",
        ["Hosting:PublicHostname"] = PublicHostname,
        ["Hosting:FrontDoorId"] = FrontDoorId,
        ["Hosting:KnownProxies:0"] = TrustedProxy.ToString(),
        ["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "false",
        ["https_port"] = "443"
    };

    private static PortalFactory HostedFactory(IPolicyStore? store = null, IPAddress? peer = null) => new()
    {
        EnvironmentName = "Production",
        ConfigurationOverrides = HostedSettings(),
        RemoteIpAddress = peer ?? TrustedProxy,
        StoreOverride = store
    };

    private static void AddFrontDoorHeaders(HttpClient browser, string scheme = "https", string host = PublicHostname)
    {
        browser.DefaultRequestHeaders.Add("X-Azure-FDID", FrontDoorId);
        browser.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.12");
        browser.DefaultRequestHeaders.Add("X-Forwarded-Proto", scheme);
        browser.DefaultRequestHeaders.Add("X-Forwarded-Host", host);
    }

    private static ServiceProvider HostingServices(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddHostedIngress();
        return services.BuildServiceProvider();
    }

    private sealed class UnhealthyStore : IPolicyStore
    {
        public int Reads { get; private set; }
        public bool NeverCompletes { get; init; }
        public CancellationToken LastCancellation { get; private set; }
        public Task<IReadOnlyList<AccessPolicy>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            LastCancellation = cancellationToken;
            return NeverCompletes
                ? new TaskCompletionSource<IReadOnlyList<AccessPolicy>>(TaskCreationOptions.RunContinuationsAsynchronously).Task
                : Task.FromException<IReadOnlyList<AccessPolicy>>(new IOException("private-health-probe-detail"));
        }
        public Task<bool> CreateAsync(AccessPolicy policy, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(string policyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> SetActiveAsync(string? policyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdatePinAsync(string policyId, PinCredential pin, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
