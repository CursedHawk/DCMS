using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Openapi;

/// <summary>
/// Per-tenant OpenAPI preview for the admin SPA. Mirrors content-api's delivery
/// assembler but resolves the tenant from the admin header (X-Dcms-Tenant) and is
/// reachable with the admin bearer token, so the preview works before any public
/// domain is attached. Cached in Redis keyed by an instance config hash (= ETag).
/// </summary>
public static class OpenApiPreviewEndpoints
{
    public static IEndpointRouteBuilder MapOpenApiPreview(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/openapi.json", async (
            HttpContext http, ITenantContext tenant, CmsDbContext db,
            OpenApiAssembler assembler, ICacheService cache, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }

            var instances = await db.PluginInstances.AsNoTracking()
                .Where(p => p.Enabled)
                .OrderBy(p => p.Slug)
                .ToListAsync(ct);

            var hash = ConfigHash(instances);
            var cacheKey = $"t:{tenantId}:openapi-admin:{hash}";
            var cached = await cache.GetAsync<string>(cacheKey, ct);
            if (cached is not null)
            {
                http.Response.Headers.ETag = $"\"{hash}\"";
                return Results.Text(cached, "application/json");
            }

            var contexts = instances.Select(p => new PluginInstanceContext(
                p.Id, p.TenantId, p.PluginId, p.Slug, p.Name, p.Description,
                JsonDocument.Parse(string.IsNullOrWhiteSpace(p.ConfigJson) ? "{}" : p.ConfigJson))).ToList();

            var doc = assembler.Build(tenant.TenantSlug ?? tenantId.ToString(), contexts);
            var json = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await cache.SetAsync(cacheKey, json, TimeSpan.FromHours(24), ct);

            http.Response.Headers.ETag = $"\"{hash}\"";
            return Results.Text(json, "application/json");
        }).RequireAuthorization();

        return app;
    }

    private static string ConfigHash(IEnumerable<PluginInstance> instances)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in instances.OrderBy(i => i.Id))
        {
            sb.Append(p.Id).Append('|').Append(p.Slug).Append('|').Append(p.PluginId)
              .Append('|').Append(p.PluginVersion).Append('|').Append(p.Name)
              .Append('|').Append(p.Description).Append('|').Append(p.ConfigJson).Append(';');
        }
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
