using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Tenancy;
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
            HttpContext http, ITenantContext tenant, CmsDbContext db, TenancyDbContext tenancy,
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

            // Advertise the tenant's verified, site-linked domains as servers so the
            // docs "try it" pipeline calls the real content endpoints (site-host
            // proxies /api on those hosts). Primary first. TenancyDbContext is
            // tenant-scoped, so this is already limited to the current tenant.
            var hostnames = await tenancy.Domains.AsNoTracking()
                .Where(d => d.VerifiedAt != null && d.SiteId != null)
                .OrderByDescending(d => d.IsPrimary)
                .ThenBy(d => d.Hostname)
                .Select(d => d.Hostname)
                .ToListAsync(ct);
            var servers = hostnames.Select(h => $"https://{h}").ToList();

            var hash = ConfigHash(instances, servers);
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

            var doc = assembler.Build(tenant.TenantSlug ?? tenantId.ToString(), contexts, servers);
            var json = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await cache.SetAsync(cacheKey, json, TimeSpan.FromHours(24), ct);

            http.Response.Headers.ETag = $"\"{hash}\"";
            return Results.Text(json, "application/json");
        }).RequireAuthorization();

        return app;
    }

    private static string ConfigHash(IEnumerable<PluginInstance> instances, IEnumerable<string> servers)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in instances.OrderBy(i => i.Id))
        {
            sb.Append(p.Id).Append('|').Append(p.Slug).Append('|').Append(p.PluginId)
              .Append('|').Append(p.PluginVersion).Append('|').Append(p.Name)
              .Append('|').Append(p.Description).Append('|').Append(p.ConfigJson).Append(';');
        }
        // Servers affect the emitted spec, so they must affect the cache key/ETag.
        sb.Append("servers:");
        foreach (var s in servers)
        {
            sb.Append(s).Append(',');
        }
        return Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
