using System.Text.Json;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Plugins;

/// <summary>
/// Public read of a plugin instance's presentation config:
/// GET /api/{slug}/_config.
///
/// Instance config is tenant-private by default. Only the keys a plugin lists in
/// <see cref="Dcms.PluginSdk.Abstractions.PluginManifest.PublicConfigKeys"/> are
/// returned, so a plugin that later gains a credential does not start leaking it.
/// Values fall back to the schema's declared defaults, which keeps the admin form
/// and the site agreeing on what "unset" means.
/// </summary>
public static class PluginConfigEndpoints
{
    public static IEndpointRouteBuilder MapPluginConfig(this IEndpointRouteBuilder app)
    {
        // A literal segment outranks the "/api/{slug}/{contentType}" content route.
        app.MapGet("/api/{slug}/_config", async (
            string slug, ITenantContext tenant, CmsDbContext cms,
            PluginRegistry registry, CancellationToken ct) =>
        {
            if (tenant.TenantId is null)
            {
                return Results.NotFound();
            }

            var instance = await cms.PluginInstances.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Slug == slug && p.Enabled, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }

            var manifest = registry.Find(instance.PluginId);
            if (manifest is null || manifest.PublicConfigKeys.Count == 0)
            {
                return Results.NotFound();
            }

            using var config = ParseOrEmpty(instance.ConfigJson);
            using var schema = ParseOrEmpty(manifest.ConfigJsonSchema);
            schema.RootElement.TryGetProperty("properties", out var properties);

            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var key in manifest.PublicConfigKeys)
            {
                if (config.RootElement.TryGetProperty(key, out var value) &&
                    value.ValueKind != JsonValueKind.Null)
                {
                    result[key] = value.Clone();
                }
                else if (properties.ValueKind == JsonValueKind.Object &&
                         properties.TryGetProperty(key, out var property) &&
                         property.TryGetProperty("default", out var fallback))
                {
                    result[key] = fallback.Clone();
                }
            }

            return Results.Ok(new
            {
                pluginId = instance.PluginId,
                slug = instance.Slug,
                config = result,
            });
        });

        return app;
    }

    private static JsonDocument ParseOrEmpty(string? json) =>
        JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
}
