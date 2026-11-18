using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Caching;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Messaging;
using Dcms.Shared.Telemetry;

namespace Dcms.AdminApi.Plugins;

/// <summary>A plugin instance changed: shared by the instance endpoints and environment deployments (PluginsConfigKind).</summary>
public static class PluginInstanceChanges
{
    /// <summary>
    /// The single place a plugin instance change leaves this service, which is why the counter
    /// lives here rather than at the three call sites: a fourth kind of change added later gets
    /// counted by construction instead of by remembering.
    /// </summary>
    public static async Task PublishChange(IEventPublisher events, ICacheService cache, DcmsMetrics metrics, PluginInstance instance, PluginInstanceChangeKind kind, CancellationToken ct)
    {
        // Plugin id, not instance id: the catalogue is a fixed, small set, whereas instances
        // are created per tenant without bound.
        metrics.PluginInstanceChanged(instance.PluginId, kind.ToString());
        // Every process's cached instance list for the tenant is now stale (PluginInstanceCache).
        await PluginInstanceCache.BumpAsync(cache, instance.TenantId, ct);
        await events.PublishAsync(Subjects.PluginInstanceChanged,
            new PluginInstanceChanged(Guid.NewGuid(), DateTimeOffset.UtcNow, instance.TenantId, instance.Id, instance.PluginId, kind), ct);
    }
}
