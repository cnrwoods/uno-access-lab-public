using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace PolicyApi;

public static class PolicyApiEndpoints
{
    public static IEndpointRouteBuilder MapPolicyApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/policies", GetPoliciesAsync);
        // The singular route now returns the same list as /api/policies.
        endpoints.MapGet("/api/policy", GetPoliciesAsync);
        endpoints.MapGet("/api/policy/active", GetActivePolicyAsync);
        endpoints.MapGet("/api/policies/{policyId}", GetPolicyAsync);
        return endpoints;
    }

    private static async Task<IResult> GetPoliciesAsync(
        IPolicyStore store, HttpContext context, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return Results.Json(await store.GetAllAsync(cancellationToken));
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return StorageUnavailable(loggerFactory, exception);
        }
    }

    private static async Task<IResult> GetActivePolicyAsync(
        IPolicyStore store, HttpContext context, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var policies = await store.GetAllAsync(cancellationToken);
            var policy = policies.SingleOrDefault(item => item.Enabled);
            return policy is null ? Results.NoContent() : Results.Json(policy);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return StorageUnavailable(loggerFactory, exception);
        }
    }

    private static async Task<IResult> GetPolicyAsync(
        string policyId, IPolicyStore store, HttpContext context, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var policies = await store.GetAllAsync(cancellationToken);
            var policy = policies.FirstOrDefault(item => string.Equals(item.PolicyId, policyId, StringComparison.OrdinalIgnoreCase));
            return policy is null ? Results.NotFound() : Results.Json(policy);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return StorageUnavailable(loggerFactory, exception);
        }
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException;

    private static IResult StorageUnavailable(ILoggerFactory loggerFactory, Exception exception)
    {
        loggerFactory.CreateLogger("PolicyApi").LogError(exception, "Unable to read policy storage.");
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Policy storage is unavailable.", detail: "Please try again later.");
    }
}
