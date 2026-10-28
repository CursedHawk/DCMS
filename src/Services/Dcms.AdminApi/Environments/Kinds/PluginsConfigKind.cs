using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.AdminApi.Plugins;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Caching;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Environments.Kinds;

/// <summary>
/// Plugin instances by slug (ADR 0026): which plugin, whether it is on, and its settings — which is
/// where content types live. Secrets stay in each environment; the data an instance holds is
/// content, and never moves.
/// </summary>
// rls: EnvironmentMigrator calls every kind inside RlsScope.Environment, the environment it works
// on; IgnoreTenantFilter drops only the tenant filter, so queries stay in that environment.
public sealed class PluginsConfigKind(
    CmsDbContext db, TenancyDbContext tenancy, IPluginCatalog catalog,
    IEventPublisher events, ICacheService cache, DcmsMetrics metrics) : IEnvironmentConfigKind
{
    public string Kind => "plugins";

    public string? Permission => PlatformPermissions.PluginsManage;

    public int Order => 20;

    private sealed record Value(string PluginId, string Name, string Description, JsonNode? Config, bool Enabled, bool AiToolsEnabled);

    public async Task<IReadOnlyList<EnvironmentConfigItem>> ExportAsync(Guid tenantId, CancellationToken ct)
    {
        var instances = await db.PluginInstances.IgnoreTenantFilter().AsNoTracking()
            .Where(p => p.TenantId == tenantId)
            .ToListAsync(ct);
        return instances.Select(p => new EnvironmentConfigItem(p.Slug, JsonSerializer.SerializeToElement(new Value(
                p.PluginId, p.Name, p.Description, JsonNode.Parse(p.ConfigJson), p.Enabled, p.AiToolsEnabled)))
            {
                Label = p.Name,
            })
            .ToList();
    }

    public async Task ApplyAsync(Guid tenantId, string key, JsonElement? value, EnvironmentApplyContext context, CancellationToken ct)
    {
        // The console cannot delete an instance either, only switch it off: its data would be
        // left without an owner.
        if (value is null) throw new EnvironmentMigrationException("environments.errors.pluginInstanceRemoved", key);

        var wanted = value.Value.Deserialize<Value>()!;
        var manifest = catalog.Find(wanted.PluginId)
                       ?? throw new EnvironmentMigrationException("environments.errors.unknownPlugin", wanted.PluginId);
        var instance = await db.PluginInstances.IgnoreTenantFilter()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Slug == key, ct);
        var created = instance is null;
        if (instance is null)
        {
            instance = new PluginInstance
            {
                Id = Guid.NewGuid(), TenantId = tenantId, PluginId = manifest.Id, PluginVersion = manifest.Version, Slug = key,
            };
            db.PluginInstances.Add(instance);
        }
        else if (instance.PluginId != wanted.PluginId)
        {
            throw new EnvironmentMigrationException("environments.errors.slugTakenByOtherPlugin", key);
        }
        var firstOfPlugin = created && !await db.PluginInstances.IgnoreTenantFilter()
            .AnyAsync(p => p.TenantId == tenantId && p.PluginId == manifest.Id, ct);
        var wasEnabled = !created && instance.Enabled;
        instance.Name = wanted.Name;
        instance.Description = wanted.Description;
        instance.ConfigJson = wanted.Config?.ToJsonString() ?? "{}";
        instance.Enabled = wanted.Enabled;
        instance.AiToolsEnabled = wanted.AiToolsEnabled;
        instance.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (firstOfPlugin) await PluginDefaultGrants.ApplyAsync(tenancy, tenantId, manifest, ct);

        var change = created ? PluginInstanceChangeKind.Created
            : wasEnabled == instance.Enabled ? PluginInstanceChangeKind.Updated
            : instance.Enabled ? PluginInstanceChangeKind.Enabled : PluginInstanceChangeKind.Disabled;
        await PluginInstanceEndpoints.PublishChange(events, cache, metrics, instance, change, ct);
    }
}
