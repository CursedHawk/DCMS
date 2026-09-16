using Dcms.AdminApi.ApiClientGen;
using Dcms.Shared.Caching;
using Dcms.Shared.Kernel.Abstractions;

namespace Dcms.AdminApi.Openapi;

/// <summary>
/// Per-tenant OpenAPI preview for the admin SPA. The same document content-api serves, resolved
/// from the admin header (X-Dcms-Tenant) and reachable with the admin bearer token, so the preview
/// works before any public domain is attached. Cached in Redis keyed by the resolver's input key
/// (= ETag).
/// </summary>
public static class OpenApiPreviewEndpoints
{
    public static IEndpointRouteBuilder MapOpenApiPreview(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/openapi.json", async (
            HttpContext http, ITenantContext tenant, TenantApiResolver resolver, ICacheService cache, CancellationToken ct) =>
        {
            if (await resolver.ResolveAsync(ct) is not { } api)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }

            var cacheKey = $"t:{tenant.TenantId}:openapi-admin:{api.InputKey}";
            var json = await cache.GetAsync<string>(cacheKey, ct);
            if (json is null)
            {
                json = api.Json;
                await cache.SetAsync(cacheKey, json, TimeSpan.FromHours(24), ct);
            }

            http.Response.Headers.ETag = $"\"{api.InputKey}\"";
            return Results.Text(json, "application/json");
        }).RequireAuthorization();

        return app;
    }
}
