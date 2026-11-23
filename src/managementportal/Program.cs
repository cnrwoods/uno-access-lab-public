using ManagementPortal.Authentication;
using ManagementPortal.Hosting;
using ManagementPortal.Storage;
using Microsoft.AspNetCore.Mvc;
using PolicyApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPortalAuthentication(builder.Configuration);
builder.Services.AddHostedIngress();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/", PortalAuthentication.AdminPolicy);
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Logout");
    options.Conventions.AllowAnonymousToPage("/AccessDenied");
    options.Conventions.ConfigureFilter(new ResponseCacheAttribute
    {
        NoStore = true,
        Location = ResponseCacheLocation.None
    });
});
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-FingAuth.Antiforgery";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.AddHttpsRedirection(options =>
{
    options.HttpsPort = builder.Environment.IsDevelopment() ? 7080 : 443;
});

// Resolve configuration when services are created, after host overrides have
// been applied. Integration checks then use isolated files and test credentials.
builder.Services.AddPolicyStorage();

// Simulated registration: a local setup script provisions this key into an
// ignored file and the sketch's ignored secrets header. Environment settings
// can supply the same key on App Service. Never return it through the API.
builder.Services.AddSingleton(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var environment = services.GetRequiredService<IHostEnvironment>();
    var sharedKeyHex = configuration["PolicySecurity:SharedKeyHex"];
    if (string.IsNullOrWhiteSpace(sharedKeyHex))
    {
        var keyFile = PolicyStoreConfiguration.GetSharedKeyFilePath(configuration, environment);
        if (File.Exists(keyFile)) sharedKeyHex = File.ReadAllText(keyFile).Trim();
    }
    // DI owns and disposes the key material. Missing configuration leaves PIN
    // creation unavailable, while fingerprint-only policies can still be used.
    return new PinProtection(sharedKeyHex);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(error => error.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsync("Something went wrong. Please try again.");
    }));
}

app.UseHostedIngress();

// The local bench API retains HTTP. Hosted application/API requests require
// HTTPS; health probes remain available without redirection or browser login.
app.UseWhen(context => !HealthEndpoints.IsHealthPath(context.Request.Path) &&
    (!app.Environment.IsDevelopment() || !context.Request.Path.StartsWithSegments("/api")), portal =>
    portal.UseHttpsRedirection());
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapHostingHealth();
// Only Razor Pages receive the PortalAdmin policy. Device authentication is a
// separate future step; this read-only API retains its current PoC contract.
app.MapPolicyApi();

app.Run();

public partial class Program { }
