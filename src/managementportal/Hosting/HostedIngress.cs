using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace ManagementPortal.Hosting;

public sealed class HostedIngressOptions
{
    public bool Enabled { get; set; }
    public string PublicHostname { get; set; } = "fingauth.conorwoods.co.uk";
    public string FrontDoorId { get; set; } = "";
    public bool TrustAppServiceProxy { get; set; }
    public string[] KnownProxies { get; set; } = [];
    public string[] KnownIPNetworks { get; set; } = [];
}

public static class HostedIngress
{
    public static IServiceCollection AddHostedIngress(this IServiceCollection services)
    {
        services.AddOptions<HostedIngressOptions>()
            .BindConfiguration("Hosting")
            .Validate(options => !options.Enabled ||
                (Uri.CheckHostName(options.PublicHostname) == UriHostNameType.Dns &&
                 !options.PublicHostname.Contains('*') && !options.PublicHostname.Contains(':')),
                "Hosting:PublicHostname must be one DNS hostname without a scheme, wildcard or port.")
            .Validate(options => !options.Enabled || Guid.TryParseExact(options.FrontDoorId, "D", out _),
                "Hosting:FrontDoorId must be the Front Door profile's GUID.")
            .Validate(options => (options.KnownProxies ?? []).All(value => IPAddress.TryParse(value, out _)),
                "Hosting:KnownProxies must contain IP addresses.")
            .Validate(options => (options.KnownIPNetworks ?? []).All(value => System.Net.IPNetwork.TryParse(value, out _)),
                "Hosting:KnownIPNetworks must contain valid IP/CIDR networks.")
            .Validate<IConfiguration>((options, configuration) =>
                !options.TrustAppServiceProxy || (options.Enabled &&
                    !string.IsNullOrWhiteSpace(configuration["WEBSITE_INSTANCE_ID"]) &&
                    Guid.TryParseExact(options.FrontDoorId, "D", out _) &&
                    (options.KnownProxies ?? []).Length == 0 &&
                    (options.KnownIPNetworks ?? []).Length == 0),
                "App Service proxy trust requires hosted ingress, WEBSITE_INSTANCE_ID and a Front Door ID; do not combine it with explicit proxies.")
            .Validate<IConfiguration>((options, configuration) => !options.Enabled ||
                !string.Equals(configuration["ASPNETCORE_FORWARDEDHEADERS_ENABLED"], "true", StringComparison.OrdinalIgnoreCase),
                "Disable ASPNETCORE_FORWARDEDHEADERS_ENABLED when using explicit hosted ingress.")
            .ValidateOnStart();

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<HostedIngressOptions>>((forwarding, configured) =>
            {
                var options = configured.Value;
                if (!options.Enabled) return;
                forwarding.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                    ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
                forwarding.ForwardLimit = 2;
                forwarding.AllowedHosts.Clear();
                forwarding.AllowedHosts.Add(options.PublicHostname);

                if (options.TrustAppServiceProxy)
                {
                    // App Service has no universal ingress proxy CIDR. This
                    // explicit mode requires the separately deployed platform
                    // AzureFrontDoor.Backend + X-Azure-FDID default-deny ACL.
                    forwarding.KnownProxies.Clear();
                    forwarding.KnownIPNetworks.Clear();
                }
                else
                {
                    foreach (var proxy in options.KnownProxies ?? [])
                        forwarding.KnownProxies.Add(IPAddress.Parse(proxy));
                    foreach (var network in options.KnownIPNetworks ?? [])
                        forwarding.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
                }
            });
        return services;
    }

    public static IApplicationBuilder UseHostedIngress(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<HostedIngressOptions>>().Value;
        if (!options.Enabled) return app;

        // Gate before processing forwarded values. This header check is defense
        // in depth, not a replacement for App Service's source/header ACL.
        app.Use(async (context, next) =>
        {
            if (!HealthEndpoints.IsHealthPath(context.Request.Path))
            {
                var identifier = context.Request.Headers["X-Azure-FDID"];
                if (identifier.Count != 1 ||
                    !string.Equals(identifier[0], options.FrontDoorId, StringComparison.OrdinalIgnoreCase))
                {
                    Forbid(context);
                    return;
                }
            }
            await next(context);
        });

        app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            if (!HealthEndpoints.IsHealthPath(context.Request.Path))
            {
                if (!string.Equals(context.Request.Host.Host, options.PublicHostname, StringComparison.OrdinalIgnoreCase) ||
                    (context.Request.Host.Port is { } port && port != 443))
                {
                    Forbid(context);
                    return;
                }
                // Emit one canonical host in redirects, including OIDC callbacks.
                context.Request.Host = new HostString(options.PublicHostname);
            }
            await next(context);
        });
        return app;
    }

    private static void Forbid(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
    }
}
