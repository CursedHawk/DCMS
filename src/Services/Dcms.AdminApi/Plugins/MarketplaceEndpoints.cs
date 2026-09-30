using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Plugins;

/// <summary>
/// The plugin marketplace.
///
/// <para><b>What this is not.</b> Tenants do not install code. The plugins listed are the ones this
/// deployment runs: compiled in (<c>Dcms.Plugins.All</c>, <c>source: "builtin"</c>) or installed by
/// the operator into the plugin directory (<c>source: "installed"</c>). "Marketplace" means
/// discovery and enablement of those.</para>
///
/// <para><b>Why it is a separate endpoint from the catalog</b>, which already returns manifests.
/// The catalog is a technical document: JSON Schemas, field definitions, dependency ids — it
/// feeds config forms and the permission matrix. This is a shopfront: what a plugin is for, what
/// it will ask permission to do, and whether this workspace already has one. Serving both from
/// one payload means every config form downloads screenshots and every shopfront downloads JSON
/// Schemas.</para>
///
/// <para>The shape is deliberately registry-flavoured — <c>source</c>, <c>version</c>, a flat
/// item list — so that a remote registry can back it later without the SPA changing.</para>
/// </summary>
public static class MarketplaceEndpoints
{
    public static IEndpointRouteBuilder MapMarketplaceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/marketplace", async (
            IPluginCatalog catalog, Dcms.PluginSdk.Runtime.PluginRegistry registry, CmsDbContext db, CancellationToken ct) =>
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
                        // Derived from Consumes: the plugins that provide what this one needs.
                        dependencies = PluginDependencies.Of(registry, m.Id)
                            .SelectMany(d => d.Providers.Select(p => new { pluginId = p, optional = d.Optional }))
                            .DistinctBy(d => d.pluginId),
                        provides = (m.Provides ?? []).Select(p => ContractIds.Of(p.Contract)),
                        consumes = (m.Consumes ?? []).Select(c => new { c.ContractId, c.Optional }),
                        addsNavEntry = m.Nav is not null,
                        instanceCount = counts?.total ?? 0,
                        enabledCount = counts?.enabled ?? 0,
                        installed = (counts?.total ?? 0) > 0,
                        source = registry.SourceOf(m.Id),
                    };
                });

            return Results.Ok(new { items });
        }).RequirePermission(PlatformPermissions.PluginsManage);

        // The developer reference: what a plugin offers other code and how to call it. Built
        // from the running registry, so it cannot drift from what the plugin actually does.
        app.MapGet("/api/admin/marketplace/{pluginId}/reference", (string pluginId, PluginRegistry registry) =>
            PluginReference.Build(registry, pluginId) is { } reference
                ? Results.Json(reference, Dcms.PluginSdk.Runtime.Contracts.ContractDescriptorBuilder.Json)
                : Results.NotFound())
            .RequirePermission(PlatformPermissions.PluginsManage);

        return app;
    }
}
