using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// Serves the per-tenant OpenAPI document (JSON/YAML) assembled from enabled
/// plugin instances, cached in Redis keyed by a config hash that doubles as the
/// ETag. A change to any instance's slug/config/version yields a new hash, so
/// the cache self-invalidates without explicit eviction.
/// </summary>
public static class OpenApiEndpoints
{
    public static IEndpointRouteBuilder MapOpenApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/openapi.json", async (HttpContext http, ITenantContext tenant, CmsDbContext db,
            OpenApiAssembler assembler, ICacheService cache, CancellationToken ct) =>
        {
            var built = await BuildAsync(tenant, db, assembler, cache, ct);
            if (built is null)
            {
                return Results.NotFound();
            }
            if (NotModified(http, built.Value.Etag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            http.Response.Headers.ETag = built.Value.Etag;
            return Results.Text(built.Value.Json, "application/json");
        });

        app.MapGet("/api/openapi.yaml", async (HttpContext http, ITenantContext tenant, CmsDbContext db,
            OpenApiAssembler assembler, ICacheService cache, CancellationToken ct) =>
        {
            var built = await BuildAsync(tenant, db, assembler, cache, ct);
            if (built is null)
            {
                return Results.NotFound();
            }
            http.Response.Headers.ETag = built.Value.Etag;
            return Results.Text(YamlConverter.JsonToYaml(built.Value.Json), "application/yaml");
        });

        // Lightweight Scalar viewer pointing at the per-tenant JSON spec.
        app.MapGet("/api/openapi", () => Results.Text(ScalarPage, "text/html"));

        return app;
    }

    private static async Task<(string Json, string Etag)?> BuildAsync(
        ITenantContext tenant, CmsDbContext db, OpenApiAssembler assembler, ICacheService cache, CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return null;
        }

        var instances = await db.PluginInstances.AsNoTracking()
            .Where(p => p.Enabled)
            .OrderBy(p => p.Slug)
            .ToListAsync(ct);

        var hash = ConfigHash(instances);
        var cacheKey = $"t:{tenantId}:openapi:{hash}";
        var cached = await cache.GetAsync<string>(cacheKey, ct);
        if (cached is not null)
        {
            return (cached, Quote(hash));
        }

        var contexts = instances.Select(p => new PluginInstanceContext(
            p.Id, p.TenantId, p.PluginId, p.Slug, p.Name, p.Description,
            JsonDocument.Parse(string.IsNullOrWhiteSpace(p.ConfigJson) ? "{}" : p.ConfigJson))).ToList();

        var doc = assembler.Build(tenant.TenantSlug ?? tenantId.ToString(), contexts);
        var json = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await cache.SetAsync(cacheKey, json, TimeSpan.FromHours(24), ct);
        return (json, Quote(hash));
    }

    private static string ConfigHash(IEnumerable<PluginInstance> instances)
    {
        var sb = new StringBuilder();
        foreach (var p in instances.OrderBy(i => i.Id))
        {
            sb.Append(p.Id).Append('|').Append(p.Slug).Append('|').Append(p.PluginId)
              .Append('|').Append(p.PluginVersion).Append('|').Append(p.Name)
              .Append('|').Append(p.Description).Append('|').Append(p.ConfigJson).Append(';');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static string Quote(string hash) => $"\"{hash}\"";

    private static bool NotModified(HttpContext http, string etag)
        => http.Request.Headers.IfNoneMatch.ToString() is { Length: > 0 } inm && inm == etag;

    private const string ScalarPage = """
        <!doctype html><html><head><meta charset="utf-8"/><title>API Reference</title></head>
        <body><script id="api-reference" data-url="/api/openapi.json"></script>
        <script src="https://cdn.jsdelivr.net/npm/@scalar/api-reference"></script></body></html>
        """;
}
