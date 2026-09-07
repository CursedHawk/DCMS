using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Plugins;

/// <summary>
/// The plugin marketplace.
///
/// <para><b>What this is not.</b> Plugins are compiled into <c>Dcms.Plugins.All</c> and there is
/// no runtime assembly loading, so "marketplace" here means discovery and enablement of what
/// ships in the binary — not installing third-party code. That would need signing, trust and
/// tenant isolation in content-api, and is a larger project than the console it would serve.</para>
///
/// <para><b>Why it is a separate endpoint from the catalog</b>, which already returns manifests.
/// The catalog is a technical document: JSON Schemas, field definitions, dependency ids — it
/// feeds config forms and the permission matrix. This is a shopfront: what a plugin is for, what
/// it will ask permission to do, and whether this workspace already has one. Serving both from
/// one payload means every config form downloads screenshots and every shopfront downloads JSON
/// Schemas.</para>
///
/// <para>The shape is deliberately registry-flavoured — <c>source</c>, <c>version</c>, a flat
/// item list — so that a remote registry can back it later without the SPA changing. Today
/// every item reports <c>source: "builtin"</c>.</para>
/// </summary>
public static class MarketplaceEndpoints
{
    public static IEndpointRouteBuilder MapMarketplaceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/marketplace", async (
            IPluginCatalog catalog, CmsDbContext db, CancellationToken ct) =>
        {
            var instances = await db.PluginInstances
                .Select(p => new { p.PluginId, p.Enabled })
                .ToListAsync(ct);

            var byPlugin = instances
                .GroupBy(p => p.PluginId)
                .ToDictionary(g => g.Key, g => new { total = g.Count(), enabled = g.Count(x => x.Enabled) });

            var items = catalog.Manifests
                .OrderBy(m => m.Category ?? "￿") // uncategorised sorts last, not first
                .ThenBy(m => m.Name)
                .Select(m =>
                {
                    byPlugin.TryGetValue(m.Id, out var counts);
                    return new
                    {
                        id = m.Id,
                        name = m.Name,
                        version = m.Version,
                        // The one-liner for a card, falling back to the full description rather
                        // than to nothing: a card with no text is worse than a long card.
                        summary = m.Summary ?? m.Description,
                        description = m.Description,
                        category = m.Category ?? "Other",
                        tags = m.Tags ?? [],
                        icon = m.IconName,
                        allowMultiple = m.AllowMultipleInstances,
                        // What adding this will ask for, stated BEFORE the operator adds it.
                        // A plugin that wants write access to content should say so on the card,
                        // not in a permission matrix somebody reads later.
                        permissions = m.Permissions
                            .Select(p => new
                            {
                                key = PlatformPermissions.ForPlugin(m.Id, p.Action),
                                p.DisplayName,
                            }),
                        contentTypes = m.ContentTypes.Select(t => t.Name),
                        dependencies = m.Dependencies.Select(d => new { d.PluginId, d.Optional }),
                        addsNavEntry = m.Nav is not null,
                        instanceCount = counts?.total ?? 0,
                        enabledCount = counts?.enabled ?? 0,
                        installed = (counts?.total ?? 0) > 0,
                        source = "builtin",
                    };
                });

            return Results.Ok(new { items });
        }).RequirePermission(PlatformPermissions.PluginsManage);

        return app;
    }
}
