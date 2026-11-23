using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PolicyApi;

namespace ManagementPortal.Tests;

internal static class TestProgram
{
    public static async Task<int> Main(string[] args)
    {
        var failed = 0;
        var passed = 0;
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); passed++; Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
        }

        await StorageConfigurationChecks.RunAsync(Check);
        await HostingChecks.RunAsync(Check);

        await Check("Anonymous browser pages lead to the public login page without contacting Keycloak", async () =>
        {
            using var factory = new PortalFactory();
            using var browser = factory.Browser();
            foreach (var path in new[] { "/", "/Policies", "/MyAccount" })
                IsRedirect(await browser.GetAsync(path), "/login");
            var login = await browser.GetAsync("/login");
            Require(login.StatusCode == HttpStatusCode.OK, "The login page is not public.");
            Require((await login.Content.ReadAsStringAsync()).Contains("__RequestVerificationToken"), "Login does not issue an antiforgery form token.");
            Require(factory.Provider.TokenRequests == 0, "Merely opening the login page requested tokens.");
        });

        await Check("Login rejects a forged POST and a real challenge uses code flow, state, nonce and S256", async () =>
        {
            using var factory = new PortalFactory();
            using var browser = factory.Browser();
            var forged = await browser.PostAsync("/login", new FormUrlEncodedContent([]));
            Require(forged.StatusCode == HttpStatusCode.BadRequest, "Login POST bypassed antiforgery validation.");
            var challenge = await factory.BeginLogin(browser, "/login?ReturnUrl=https://attacker.invalid/");
            Require(challenge["response_type"] == "code", "Login does not use authorization code flow.");
            Require(challenge["client_id"] == "fingerprint-management-plane", "The configured client was not used.");
            Require(challenge["redirect_uri"] == "https://localhost:7080/signin-oidc", "OIDC callback URI is incorrect.");
            Require(challenge["code_challenge_method"] == "S256", "PKCE S256 is missing.");
            Require(!string.IsNullOrWhiteSpace(challenge["code_challenge"]) && !string.IsNullOrWhiteSpace(challenge["state"]) &&
                !string.IsNullOrWhiteSpace(challenge["nonce"]), "PKCE, state or nonce is missing.");
        });

        await Check("A signed OIDC callback creates a secure administrator session and lands at the overview", async () =>
        {
            using var factory = new PortalFactory("platform-admin");
            using var browser = factory.Browser();
            var callback = await factory.Login(browser, "/login?ReturnUrl=/Policies");
            IsRedirect(callback, "/");
            Require(factory.Provider.TokenRequests == 1 && factory.Provider.PkceVerified, "The code was not redeemed with a matching PKCE verifier.");
            var ticket = factory.ReadTicket(callback);
            Require(ticket.Principal.IsInRole("platform-admin"), "The flat roles claim was not mapped as the role claim.");
            Require(ticket.Properties.GetTokenValue("id_token") is not null, "The ID token required for logout was discarded.");
            Require(ticket.Properties.GetTokenValue("access_token") is null && ticket.Properties.GetTokenValue("refresh_token") is null,
                "Unneeded access or refresh tokens were retained in the session.");
            var sessionHeader = callback.Headers.GetValues("Set-Cookie").First(x => x.StartsWith("__Host-FingAuth.Portal=", StringComparison.Ordinal));
            Require(sessionHeader.Contains("secure", StringComparison.OrdinalIgnoreCase) && sessionHeader.Contains("httponly", StringComparison.OrdinalIgnoreCase) &&
                sessionHeader.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase), "The session cookie lacks the expected secure attributes.");
            var overview = await browser.GetAsync("/");
            Require(overview.StatusCode == HttpStatusCode.OK && (await overview.Content.ReadAsStringAsync()).Contains("Manage access policies"), "The existing overview is unavailable after login.");
            Require((await browser.GetAsync("/Policies")).StatusCode == HttpStatusCode.OK, "The administrator cannot open policies.");
        });

        await Check("My Account safely displays signed profile fields and distinguishes missing email from unverified email", async () =>
        {
            const string displayName = "<script>profile-probe</script>";
            using (var factory = new PortalFactory("platform-admin", "unrelated-profile-role"))
            using (var browser = factory.Browser())
            {
                factory.Provider.ProfileClaims = new Dictionary<string, object>
                {
                    ["name"] = displayName,
                    ["given_name"] = "Profile Given",
                    ["family_name"] = "Profile Family",
                    ["preferred_username"] = "profile-username",
                    ["email"] = "profile@example.test",
                    ["email_verified"] = true
                };
                var callback = await factory.Login(browser);
                var response = await browser.GetAsync("/MyAccount");
                Require(response.StatusCode == HttpStatusCode.OK, "An administrator cannot open My Account.");
                var html = await response.Content.ReadAsStringAsync();
                foreach (var (label, expected) in new[]
                {
                    ("Display name", displayName), ("Username", "profile-username"),
                    ("First name", "Profile Given"), ("Last name", "Profile Family"),
                    ("Email", "profile@example.test"), ("Email verification", "Verified")
                })
                    Require(ProfileValue(html, label) == expected, $"My Account did not display the signed {label} value.");
                Require(html.Contains("&lt;script&gt;profile-probe&lt;/script&gt;", StringComparison.Ordinal) &&
                    !html.Contains(displayName, StringComparison.Ordinal), "A profile field was rendered as executable HTML.");
                Require(html.Contains("Administrator", StringComparison.Ordinal) && !html.Contains("unrelated-profile-role", StringComparison.Ordinal),
                    "My Account exposed an unknown role or omitted the friendly administrator label.");
                var idToken = factory.ReadTicket(callback).Properties.GetTokenValue("id_token")!;
                Require(!html.Contains(idToken, StringComparison.Ordinal) && !html.Contains("integration-user", StringComparison.Ordinal) &&
                    !html.Contains("integration-access-token", StringComparison.Ordinal) && !html.Contains("integration-refresh-token", StringComparison.Ordinal),
                    "My Account exposed a subject identifier or authentication token.");
            }

            using (var factory = new PortalFactory("platform-admin"))
            using (var browser = factory.Browser())
            {
                factory.Provider.ProfileClaims = new Dictionary<string, object>
                {
                    ["given_name"] = "Sparse Given", ["family_name"] = "Sparse Family", ["email_verified"] = false
                };
                await factory.Login(browser);
                var html = await browser.GetStringAsync("/MyAccount");
                Require(ProfileValue(html, "Display name") == "Sparse Given Sparse Family", "Missing display name did not fall back to the available names.");
                Require(ProfileValue(html, "Email") == "Not provided" && ProfileValue(html, "Email verification") == "Not provided",
                    "An absent email address was represented as an unverified address.");
                Require(ProfileValue(html, "Username") == "Not provided", "A missing username was not handled gracefully.");
            }

            foreach (var verification in new bool?[] { null, false })
            {
                using var factory = new PortalFactory("platform-admin");
                using var browser = factory.Browser();
                factory.Provider.ProfileClaims = new Dictionary<string, object>
                {
                    ["email"] = "email-state@example.test"
                };
                if (verification.HasValue) factory.Provider.ProfileClaims["email_verified"] = verification.Value;
                await factory.Login(browser);
                var html = await browser.GetStringAsync("/MyAccount");
                Require(ProfileValue(html, "Email verification") == (verification.HasValue ? "Not verified" : "Not provided"),
                    "Explicitly false email verification was not distinguished from missing verification information.");
            }
        });

        foreach (var role in new[] { "platform-operator", "platform-auditor", "unrelated-role", "" })
        {
            await Check($"A session with {(role.Length == 0 ? "no application role" : role)} cannot access portal administration", async () =>
            {
                using var factory = new PortalFactory(role.Length == 0 ? [] : [role]);
                using var browser = factory.Browser();
                await factory.Login(browser);
                foreach (var path in new[] { "/", "/Policies", "/MyAccount" })
                {
                    var denied = await browser.GetAsync(path);
                    if (denied.StatusCode == HttpStatusCode.Redirect)
                    {
                        IsRedirect(denied, "/access-denied");
                        denied = await browser.GetAsync(denied.Headers.Location);
                    }
                    Require(denied.StatusCode == HttpStatusCode.Forbidden, $"{path} did not deny the insufficient role.");
                }
            });
        }

        foreach (var badNonce in new[] { false, true })
        {
            await Check($"An invalid {(badNonce ? "nonce" : "state")} callback cannot create a browser session", async () =>
            {
                using var factory = new PortalFactory("platform-admin");
                using var browser = factory.Browser();
                var challenge = await factory.BeginLogin(browser);
                factory.Provider.InvalidNonce = badNonce;
                var callback = await factory.CompleteLogin(browser, challenge, badNonce ? challenge["state"]! : "forged-state");
                Require(!factory.HasSessionCookie(callback), "An invalid callback issued an authentication cookie.");
                IsRedirect(await browser.GetAsync("/"), "/login");
            });
        }

        await Check("Untrusted, wrong-audience and expired ID tokens fail safely without issuing a session", async () =>
        {
            foreach (var fault in new[] { TokenFault.WrongIssuer, TokenFault.WrongAudience, TokenFault.WrongSignature, TokenFault.Expired })
            {
                using var factory = new PortalFactory("platform-admin");
                using var browser = factory.Browser();
                var challenge = await factory.BeginLogin(browser);
                factory.Provider.Fault = fault;
                var callback = await factory.CompleteLogin(browser, challenge, challenge["state"]!);
                IsRedirect(callback, "/login?error=authentication");
                Require(!factory.HasSessionCookie(callback), $"An ID token with {fault} issued a session cookie.");
                IsRedirect(await browser.GetAsync("/"), "/login");
                var errorPage = await browser.GetStringAsync(callback.Headers.Location);
                Require(!errorPage.Contains("integration-access-token", StringComparison.Ordinal) &&
                    !errorPage.Contains("integration-refresh-token", StringComparison.Ordinal) &&
                    !errorPage.Contains("Microsoft.IdentityModel", StringComparison.Ordinal) &&
                    !errorPage.Contains("SecurityTokenInvalid", StringComparison.Ordinal),
                    $"The {fault} error page disclosed token or validation exception details.");
            }
        });

        await Check("Logout requires a POST with antiforgery, revokes the local cookie and requests Keycloak logout", async () =>
        {
            using var factory = new PortalFactory("platform-admin");
            using var browser = factory.Browser();
            await factory.Login(browser);
            await browser.GetAsync("/logout");
            Require((await browser.GetAsync("/")).StatusCode == HttpStatusCode.OK, "A GET request signed the browser out.");
            var forged = await browser.PostAsync("/logout", new FormUrlEncodedContent([]));
            Require(forged.StatusCode == HttpStatusCode.BadRequest, "Logout POST bypassed antiforgery validation.");
            var token = await Antiforgery(browser, "/");
            var logout = await browser.PostAsync("/logout", Form(("__RequestVerificationToken", token)));
            Require(logout.StatusCode == HttpStatusCode.Redirect && logout.Headers.Location?.AbsolutePath == "/logout", "The app did not initiate Keycloak logout.");
            var parameters = QueryHelpers.ParseQuery(logout.Headers.Location!.Query);
            Require(!string.IsNullOrWhiteSpace(parameters["id_token_hint"]), "Keycloak logout does not receive the ID token hint.");
            Require(parameters["post_logout_redirect_uri"] == "https://localhost:7080/signout-callback-oidc", "Logout callback URI is incorrect.");
            IsRedirect(await browser.GetAsync("/"), "/login");
        });

        await Check("Policy writes require both the administrator role and a valid antiforgery token", async () =>
        {
            static Task<HttpResponseMessage> CreatePolicy(HttpClient browser, string? antiforgery = null)
            {
                var fields = new List<KeyValuePair<string, string>>
                {
                    new("Input.PolicyId", "AUTH-TEST"),
                    new("Input.AccessMode", "fingerprintOnly"),
                    new("Input.AllowedMinuteParity", "any"),
                    new("Input.StartTime", "00:00"),
                    new("Input.EndTime", "24:00"),
                    new("Input.MakeActive", "true")
                };
                if (antiforgery is not null) fields.Add(new("__RequestVerificationToken", antiforgery));
                return browser.PostAsync("/Policies?handler=Create", new FormUrlEncodedContent(fields));
            }

            using var factory = new PortalFactory("platform-admin");
            using var anonymous = factory.Browser();
            using var administrator = factory.Browser();
            var store = factory.Services.GetRequiredService<IPolicyStore>();
            Require(!(await store.GetAllAsync()).Any(), "The isolated policy store was not initially empty.");

            IsRedirect(await CreatePolicy(anonymous), "/login");
            Require(!(await store.GetAllAsync()).Any(), "An anonymous policy POST changed the store.");

            await factory.Login(administrator);
            var forged = await CreatePolicy(administrator);
            Require(forged.StatusCode == HttpStatusCode.BadRequest, "An administrator policy POST bypassed antiforgery validation.");
            Require(!(await store.GetAllAsync()).Any(), "A policy POST without antiforgery changed the store.");

            using (var unprivilegedFactory = new PortalFactory("platform-operator"))
            using (var unprivileged = unprivilegedFactory.Browser())
            {
                await unprivilegedFactory.Login(unprivileged);
                var token = await Antiforgery(unprivileged, "/access-denied");
                IsRedirect(await CreatePolicy(unprivileged, token), "/access-denied");
                var unprivilegedStore = unprivilegedFactory.Services.GetRequiredService<IPolicyStore>();
                Require(!(await unprivilegedStore.GetAllAsync()).Any(), "A valid-antiforgery POST without the administrator role changed the store.");
            }

            var validToken = await Antiforgery(administrator, "/Policies");
            IsRedirect(await CreatePolicy(administrator, validToken), "/Policies");
            var policies = await store.GetAllAsync();
            Require(policies.Count == 1 && policies.Single().PolicyId == "AUTH-TEST", "An authorized policy POST did not create exactly the requested policy.");
        });

        await Check("HTTP portal traffic upgrades to HTTPS while UNO API reads do not receive a login or TLS redirect", async () =>
        {
            using var factory = new PortalFactory();
            using var browser = factory.Browser("http://localhost:5080");
            var portal = await browser.GetAsync("/");
            Require(portal.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect &&
                portal.Headers.Location?.Scheme == "https", "HTTP portal traffic did not upgrade to HTTPS.");
            var api = await browser.GetAsync("/api/policy/active");
            Require(api.StatusCode == HttpStatusCode.NoContent, $"An anonymous device read returned {(int)api.StatusCode}; expected 204, with no browser redirect.");
            Require(api.Headers.Location is null, "The device read received a browser redirect.");
        });

        if (failed == 0 && args.Contains("--export-preview", StringComparer.Ordinal))
            await ExportPreview();

        Console.WriteLine($"{passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task ExportPreview()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "managementportal", "ManagementPortal.csproj")))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Cannot locate the repository for preview export.");

        var previewPath = Path.Combine(directory.FullName, "build", "portal-preview");
        Directory.CreateDirectory(Path.Combine(previewPath, "css"));
        using var factory = new PortalFactory("platform-admin");
        using var browser = factory.Browser();
        var login = await browser.GetStringAsync("/login");
        await factory.Login(browser);
        var overview = await browser.GetStringAsync("/");
        await File.WriteAllTextAsync(Path.Combine(previewPath, "login.html"), login);
        await File.WriteAllTextAsync(Path.Combine(previewPath, "overview.html"), overview);
        File.Copy(Path.Combine(directory.FullName, "src", "managementportal", "wwwroot", "css", "site.css"),
            Path.Combine(previewPath, "css", "site.css"), overwrite: true);
        Console.WriteLine($"Rendered preview pages exported to {previewPath}");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string ProfileValue(string html, string label)
    {
        var field = Regex.Match(html,
            $"<dt(?:\\s[^>]*)?>\\s*{Regex.Escape(label)}\\s*</dt>\\s*<dd(?:\\s[^>]*)?>(.*?)</dd>",
            RegexOptions.Singleline);
        Require(field.Success, $"My Account did not contain the {label} field.");
        return WebUtility.HtmlDecode(Regex.Replace(field.Groups[1].Value, "<[^>]+>", "")).Trim();
    }

    internal static void IsRedirect(HttpResponseMessage response, string expectedPath)
    {
        var target = response.Headers.Location;
        var path = target is null ? null : target.IsAbsoluteUri ? target.PathAndQuery : target.OriginalString;
        Require(response.StatusCode == HttpStatusCode.Redirect && path == expectedPath,
            $"Expected redirect to {expectedPath}; received {(int)response.StatusCode} to {path}.");
    }

    internal static async Task<string> Antiforgery(HttpClient browser, string path)
    {
        var response = await browser.GetAsync(path);
        Require(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden, "The antiforgery source page was unavailable.");
        var html = await response.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Require(token.Success, "The page did not contain an antiforgery form token.");
        return WebUtility.HtmlDecode(token.Groups[1].Value);
    }

    internal static FormUrlEncodedContent Form(params (string Key, string Value)[] pairs) =>
        new(pairs.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)));
}

internal sealed class PortalFactory(params string[] roles) : WebApplicationFactory<global::Program>
{
    private readonly string dataPath = Path.Combine(Path.GetTempPath(), $"uno-portal-auth-tests-{Guid.NewGuid():N}.json");
    public FakeProvider Provider { get; } = new(roles);
    public IPolicyStore? StoreOverride { get; init; }
    public string EnvironmentName { get; init; } = "Development";
    public Dictionary<string, string?> ConfigurationOverrides { get; init; } = [];
    public IPAddress? RemoteIpAddress { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
            ["Authentication:Authority"] = FakeProvider.Issuer,
            ["Authentication:ClientId"] = "fingerprint-management-plane",
            ["Authentication:ClientSecret"] = "integration-only-secret",
            ["PolicyStore:Provider"] = "File",
            ["Hosting:Enabled"] = "false",
            ["Hosting:TrustAppServiceProxy"] = "false",
            ["Hosting:KnownProxies"] = null,
            ["Hosting:KnownIPNetworks"] = null,
            ["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "false",
            ["PolicyStore:FilePath"] = dataPath,
            ["PolicySecurity:SharedKeyHex"] = new string('1', 64),
            ["https_port"] = "7080"
            });
            config.AddInMemoryCollection(ConfigurationOverrides);
        });
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            if (RemoteIpAddress is not null)
                services.AddSingleton<IStartupFilter>(new RemotePeerStartupFilter(RemoteIpAddress));
            if (StoreOverride is not null)
            {
                services.RemoveAll<IPolicyStore>();
                services.AddSingleton(StoreOverride);
            }
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.PostConfigure<OpenIdConnectOptions>("Keycloak", options =>
            {
                options.Configuration = Provider.Configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(Provider.Configuration);
                options.Backchannel = new HttpClient(Provider);
            });
        });
    }

    public HttpClient Browser(string origin = "https://localhost:7080") => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(origin), AllowAutoRedirect = false, HandleCookies = true
    });

    public async Task<Dictionary<string, string?>> BeginLogin(HttpClient browser, string path = "/login")
    {
        var token = await TestProgram.Antiforgery(browser, "/login");
        var response = await browser.PostAsync(path, TestProgram.Form(("__RequestVerificationToken", token)));
        TestProgram.Require(response.StatusCode == HttpStatusCode.Redirect, "The login button did not initiate OIDC.");
        var target = response.Headers.Location!;
        TestProgram.Require(target.Host == "identity.integration.invalid" && target.AbsolutePath == "/authorize", "Login did not redirect to the configured provider.");
        var parameters = QueryHelpers.ParseQuery(target.Query).ToDictionary(x => x.Key, x => (string?)x.Value.ToString());
        Provider.Nonce = parameters["nonce"]!;
        Provider.Challenge = parameters["code_challenge"]!;
        return parameters;
    }

    public Task<HttpResponseMessage> CompleteLogin(HttpClient browser, Dictionary<string, string?> challenge, string state) =>
        browser.PostAsync("/signin-oidc", TestProgram.Form(("code", "integration-code"), ("state", state)));

    public async Task<HttpResponseMessage> Login(HttpClient browser, string path = "/login")
    {
        var challenge = await BeginLogin(browser, path);
        return await CompleteLogin(browser, challenge, challenge["state"]!);
    }

    public bool HasSessionCookie(HttpResponseMessage response) => response.Headers.TryGetValues("Set-Cookie", out var values) &&
        values.Any(value => value.StartsWith("__Host-FingAuth.Portal=", StringComparison.Ordinal) && !value.StartsWith("__Host-FingAuth.Portal=;", StringComparison.Ordinal));

    public AuthenticationTicket ReadTicket(HttpResponseMessage response)
    {
        var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(header => header.Split(';')[0]));
        var cookie = options.CookieManager.GetRequestCookie(context, options.Cookie.Name!);
        return options.TicketDataFormat.Unprotect(cookie!) ?? throw new InvalidOperationException("The session cookie could not be decoded with the app's own protection keys.");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(dataPath)) File.Delete(dataPath);
    }

    private sealed class RemotePeerStartupFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

internal enum TokenFault { None, WrongIssuer, WrongAudience, WrongSignature, Expired }

internal sealed class FakeProvider : HttpMessageHandler
{
    public const string Issuer = "https://identity.integration.invalid/realms/fingauth";
    private readonly RSA signingRsa = RSA.Create(2048);
    private readonly RSA untrustedRsa = RSA.Create(2048);
    private readonly string[] roles;
    private readonly RsaSecurityKey signingKey;
    public OpenIdConnectConfiguration Configuration { get; }
    public string Nonce { get; set; } = "";
    public string Challenge { get; set; } = "";
    public bool InvalidNonce { get; set; }
    public TokenFault Fault { get; set; }
    public Dictionary<string, object>? ProfileClaims { get; set; }
    public int TokenRequests { get; private set; }
    public bool PkceVerified { get; private set; }

    public FakeProvider(string[] roles)
    {
        this.roles = roles;
        signingKey = new RsaSecurityKey(signingRsa) { KeyId = "integration-test-signing-key" };
        Configuration = new OpenIdConnectConfiguration
        {
            Issuer = Issuer,
            AuthorizationEndpoint = "https://identity.integration.invalid/authorize",
            TokenEndpoint = "https://identity.integration.invalid/token",
            EndSessionEndpoint = "https://identity.integration.invalid/logout"
        };
        Configuration.SigningKeys.Add(signingKey);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        TestProgram.Require(request.RequestUri?.AbsoluteUri == Configuration.TokenEndpoint, "An unexpected provider network request occurred.");
        TokenRequests++;
        var parameters = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
        TestProgram.Require(parameters["grant_type"] == "authorization_code" && parameters["code"] == "integration-code", "The token request did not redeem the expected authorization code.");
        TestProgram.Require(parameters["client_secret"] == "integration-only-secret", "The server did not authenticate as a confidential client.");
        var verifier = parameters["code_verifier"].ToString();
        PkceVerified = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) == Challenge;
        TestProgram.Require(PkceVerified, "The PKCE verifier does not match the original challenge.");
        var now = DateTime.UtcNow;
        var key = Fault == TokenFault.WrongSignature
            ? new RsaSecurityKey(untrustedRsa) { KeyId = signingKey.KeyId }
            : signingKey;
        var claims = ProfileClaims is null
            ? new Dictionary<string, object> { ["name"] = "Integration user" }
            : new Dictionary<string, object>(ProfileClaims);
        claims["sub"] = "integration-user";
        claims["nonce"] = InvalidNonce ? "incorrect-nonce" : Nonce;
        claims["roles"] = roles;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Fault == TokenFault.WrongIssuer ? "https://untrusted.integration.invalid/realms/fingauth" : Issuer,
            Audience = Fault == TokenFault.WrongAudience ? "a-different-application" : "fingerprint-management-plane",
            IssuedAt = now.AddMinutes(-20),
            NotBefore = now.AddMinutes(-20),
            Expires = Fault == TokenFault.Expired ? now.AddMinutes(-10) : now.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                id_token = token, access_token = "integration-access-token", refresh_token = "integration-refresh-token",
                token_type = "Bearer", expires_in = 300
            }), Encoding.UTF8, "application/json")
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            signingRsa.Dispose();
            untrustedRsa.Dispose();
        }
        base.Dispose(disposing);
    }
}
