using Dcms.Plugins.DynamicApps.Data;
using Dcms.Shared.Data.DynamicApps;
using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace Dcms.Plugins.DynamicApps.Metadata;

/// <summary>An app's published tables, for tables links from other environments (ADR 0028).</summary>
internal sealed class DynamicAppsLinkTables(AppsDbContext db, IMemoryCache cache) : IEnvironmentLinkTables
{
    public string PluginId => DynamicAppsPlugin.PluginId;

    public async Task<IReadOnlyList<string>> TablesAsync(Guid tenantId, Guid instanceId, CancellationToken ct) =>
        (await PublishedModels.LoadAsync(db, cache, tenantId, instanceId, ct))?.ByName.Keys.Order(StringComparer.Ordinal).ToList() ?? [];
}
