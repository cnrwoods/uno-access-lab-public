using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ManagementPortal.Authentication;

public static class PortalAuthentication
{
    public const string OidcScheme = "Keycloak";
    public const string AdminPolicy = "PortalAdmin";
    public const string AdminRole = "platform-admin";

    public static IServiceCollection AddPortalAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PortalAuthenticationSettings>()
            .Bind(configuration.GetSection("Authentication"))
            .Validate(settings => Uri.TryCreate(settings.Authority, UriKind.Absolute, out var authority)
                && authority.Scheme == Uri.UriSchemeHttps
                && string.IsNullOrEmpty(authority.Query) && string.IsNullOrEmpty(authority.Fragment),
                "Authentication:Authority must be an absolute HTTPS realm URL.")
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.ClientId),
                "Authentication:ClientId is required.")
            .Validate(settings => !string.IsNullOrWhiteSpace(settings.ClientSecret),
                "Authentication:ClientSecret is missing. Run scripts/Initialize-PortalAuthentication.ps1 to store it in .NET user secrets.")
            .ValidateOnStart();

        // Cookie challenges show our local login page. Only its explicit POST
        // button challenges Keycloak, so visiting a protected page never skips it.
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.Cookie.Name = "__Host-FingAuth.Portal";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/access-denied";
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.Redirect("/login");
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.Redirect("/access-denied");
                return Task.CompletedTask;
            };
        })
        .AddOpenIdConnect(OidcScheme, _ => { });

        services.AddOptions<OpenIdConnectOptions>(OidcScheme)
            .Configure<Microsoft.Extensions.Options.IOptions<PortalAuthenticationSettings>>((options, settings) =>
            {
                options.Authority = settings.Value.Authority;
                options.ClientId = settings.Value.ClientId;
                options.ClientSecret = settings.Value.ClientSecret;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.ResponseMode = OpenIdConnectResponseMode.FormPost;
                options.UsePkce = true;
                options.RequireHttpsMetadata = true;
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";
                options.SignedOutRedirectUri = "/login";
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "roles";
                // The flat roles claim was configured in the ID token. No extra
                // UserInfo call or access/refresh token is needed by this portal.
                options.GetClaimsFromUserInfoEndpoint = false;
                options.SaveTokens = true;
                options.Events.OnTicketReceived = context =>
                {
                    // Keep only the ID token used as the logout hint. Tokens are
                    // protected inside the HttpOnly cookie and never rendered.
                    if (context.Properties is { } properties)
                        properties.StoreTokens(properties.GetTokens()
                            .Where(token => token.Name == "id_token").ToArray());
                    context.ReturnUri = "/";
                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect("/login?error=authentication");
                    return Task.CompletedTask;
                };
                options.Events.OnAuthenticationFailed = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect("/login?error=authentication");
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorization(options =>
            options.AddPolicy(AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(AdminRole)));
        return services;
    }
}

public sealed class PortalAuthenticationSettings
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}
