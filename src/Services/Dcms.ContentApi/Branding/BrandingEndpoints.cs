using System.Text.Json;
using Dcms.Plugins.Branding;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Branding;

/// <summary>
/// Public branding read: GET /api/{slug}/branding (the Branding plugin).
/// The plugin SDK does not mount custom plugin routes, so — like the analytics
/// beacon and form submissions — the endpoint lives here and returns exactly the
/// shape the plugin's OpenAPI fragment documents, read from the resolved plugin
/// instance's config. The logo/favicon are stored as media asset ids and resolved
/// to servable URLs here via <see cref="IMediaResolver"/> (same as content MediaRefs).
/// </summary>
public static class BrandingEndpoints
{
    /// <summary>
    /// CORS policy for the public branding read: any origin may GET (no
    /// credentials), so externally hosted tenant sites can fetch their branding.
    /// Mirrors the analytics beacon / form-submit shape. Registered in Program.cs.
    /// </summary>
    public const string ReadCorsPolicy = "branding-read";

    public static IEndpointRouteBuilder MapBranding(this IEndpointRouteBuilder app)
    {
        // A literal segment outranks the "/api/{slug}/{contentType}" content route.
        app.MapGet("/api/{slug}/branding", async (
            string slug, ITenantContext tenant, CmsDbContext cms, IMediaResolver media, CancellationToken ct) =>
        {
            if (tenant.TenantId is null)
            {
                return Results.NotFound();
            }

            var instance = await cms.PluginInstances.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Slug == slug && p.PluginId == BrandingPlugin.PluginId && p.Enabled, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }

            using var config = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(instance.ConfigJson) ? "{}" : instance.ConfigJson);
            var branding = BrandingPlugin.ReadBranding(config);

            // Public section only — branding.PrivateItems is tenant-private and
            // must never be written to this anonymous response.
            return Results.Ok(new
            {
                name = branding.Name,
                tagline = branding.Tagline,
                logoUrl = await ResolveUrlAsync(media, branding.LogoAssetId, ct),
                logoDarkUrl = await ResolveUrlAsync(media, branding.LogoDarkAssetId, ct),
                faviconUrl = await ResolveUrlAsync(media, branding.FaviconAssetId, ct),
                primaryColor = branding.PrimaryColor,
                secondaryColor = branding.SecondaryColor,
                items = branding.Items,
            });
        }).RequireCors(ReadCorsPolicy);

        return app;
    }

    /// <summary>
    /// Resolves a stored media asset id to a servable URL (the original rendition).
    /// Returns null when unset, malformed, or the asset no longer exists — so an
    /// unconfigured or deleted logo simply yields a null URL rather than an error.
    /// </summary>
    private static async Task<string?> ResolveUrlAsync(IMediaResolver media, string? assetId, CancellationToken ct)
    {
        if (!Guid.TryParse(assetId, out var id))
        {
            return null;
        }

        var asset = await media.ResolveAsync(id, ct);
        if (asset is null)
        {
            return null;
        }

        return asset.VariantUrls.TryGetValue("original", out var url) ? url : asset.VariantUrls.Values.FirstOrDefault();
    }
}
