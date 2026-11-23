using PolicyApi;

namespace ManagementPortal.Hosting;

public static class HealthEndpoints
{
    public static bool IsHealthPath(PathString path)
    {
        var value = path.Value?.TrimEnd('/');
        return string.Equals(value, "/health/live", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "/health/ready", StringComparison.OrdinalIgnoreCase);
    }

    public static IEndpointRouteBuilder MapHostingHealth(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods("/health/live", ["GET", "HEAD"], (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.StatusCode(StatusCodes.Status200OK);
        }).AllowAnonymous();

        endpoints.MapMethods("/health/ready", ["GET", "HEAD"], async (
            HttpContext context, IPolicyStore store, ILoggerFactory loggerFactory) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await store.GetAllAsync(timeout.Token).WaitAsync(timeout.Token);
                return Results.StatusCode(StatusCodes.Status200OK);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                loggerFactory.CreateLogger("HostingHealth").LogWarning(exception,
                    "Policy storage readiness check failed.");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }).AllowAnonymous();
        return endpoints;
    }
}
