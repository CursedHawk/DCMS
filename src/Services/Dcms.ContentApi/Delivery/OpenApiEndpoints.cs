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
            var built = await BuildAsync(tenant, db, assembler, cache, http.RequestServices, ct);
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
            var built = await BuildAsync(tenant, db, assembler, cache, http.RequestServices, ct);
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
        ITenantContext tenant, CmsDbContext db, OpenApiAssembler assembler, ICacheService cache, IServiceProvider services, CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return null;
        }

        var instances = await db.PluginInstances.AsNoTracking()
            .Where(p => p.Enabled)
            .OrderBy(p => p.Slug)
            .ToListAsync(ct);

        // Part of the key, not just the document: the tag index appears in the spec
        // the moment a tenant publishes its first tag, and a key that ignored it
        // would serve the pre-tag document for another 24 hours.
        var tagging = await TagDeliveryEndpoints.HasTagsAsync(tenantId, db, cache, ct);
        var contexts = instances.Select(p => new PluginInstanceContext(
            p.Id, p.TenantId, p.PluginId, p.Slug, p.Name, p.Description,
            JsonDocument.Parse(string.IsNullOrWhiteSpace(p.ConfigJson) ? "{}" : p.ConfigJson))).ToList();

        // A plugin whose API is runtime data (a published Dynamic Apps model) says so through
        // its API version; without it a publish would keep serving the old document for a day.
        var versions = await assembler.ApiVersionsAsync(contexts, services, ct);
        var hash = PluginInstanceFingerprint.Of(instances) + (tagging ? "t" : "")
                   + (versions.Length > 0 ? PluginInstanceFingerprint.Hash(versions)[..12] : "");
        var cacheKey = $"t:{tenantId}:openapi:{hash}";
        var cached = await cache.GetAsync<string>(cacheKey, ct);
        if (cached is not null)
        {
            return (cached, Quote(hash));
        }

        var doc = await assembler.BuildAsync(tenant.TenantSlug ?? tenantId.ToString(), contexts, services, tagging: tagging, ct: ct);
        var json = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await cache.SetAsync(cacheKey, json, TimeSpan.FromHours(24), ct);
        return (json, Quote(hash));
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
