using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Shared.Audit;
using Dcms.Shared.Caching;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Messaging;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Plugins;

/// <summary>
/// <c>dcms.instances@1</c>: a plugin installs an instance of itself, in the tenant and environment
/// it runs in, as the console's install would — the same default grants and change events —
/// and it is audited as the plugin's doing. Never another plugin's, never over another app.
/// </summary>
// rls: the caller's tenant and environment; IgnoreTenantFilter drops only the tenant filter and
// every query names the caller's tenant.
public sealed class AdminPluginInstances(
    IPluginContext caller, CmsDbContext db, TenancyDbContext tenancy, IPluginCatalog catalog,
    IEventPublisher events, ICacheService cache, DcmsMetrics metrics, IAuditRecorder audit) : IPluginInstances
{
    public async Task<InstanceRef> EnsureOwnAsync(InstanceEnsure input, CancellationToken ct)
    {
        var slug = input.Slug?.Trim() ?? "";
        if (PluginInstanceSlugs.Problem(slug) is { } problem)
        {
            throw new ContractValidationException(problem);
        }
        var name = input.Name?.Trim() is { Length: > 0 and <= 120 } n ? n : throw new ContractValidationException("A name of 1 to 120 characters.");
        var existing = await db.PluginInstances.IgnoreTenantFilter().FirstOrDefaultAsync(p => p.TenantId == caller.TenantId && p.Slug == slug, ct);
        if (existing is not null)
        {
            if (existing.PluginId != caller.PluginId)
            {
                throw new ContractConflictException($"Another app has the address '{slug}'.");
            }
            if (!existing.Enabled)
            {
                existing.Enabled = true;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                await PluginInstanceEndpoints.PublishChange(events, cache, metrics, existing, PluginInstanceChangeKind.Enabled, ct);
            }
            return new InstanceRef(existing.Id, existing.Slug, Created: false);
        }

        var manifest = catalog.Find(caller.PluginId) ?? throw new InvalidOperationException($"Plugin '{caller.PluginId}' is not in the catalog.");
        if (catalog.Manifests.Any(m => m.ReservedSlugs?.Contains(slug) == true)
            || await Tenancy.EnvironmentLinkRules.AppLinkedAsync(tenancy, caller.TenantId, slug, ct))
        {
            throw new ContractConflictException($"The address '{slug}' is taken.");
        }
        var instance = new PluginInstance
        {
            Id = Guid.NewGuid(),
            TenantId = caller.TenantId,
            PluginId = manifest.Id,
            PluginVersion = manifest.Version,
            Slug = slug,
            Name = name,
            Description = input.Description is { Length: > 0 } d ? d[..Math.Min(d.Length, 500)] : manifest.Description,
        };
        var firstOfPlugin = !await db.PluginInstances.IgnoreTenantFilter().AnyAsync(p => p.TenantId == caller.TenantId && p.PluginId == manifest.Id, ct);
        db.PluginInstances.Add(instance);
        await db.SaveChangesAsync(ct);
        if (firstOfPlugin)
        {
            await PluginDefaultGrants.ApplyAsync(tenancy, caller.TenantId, manifest, ct);
        }
        await PluginInstanceEndpoints.PublishChange(events, cache, metrics, instance, PluginInstanceChangeKind.Created, ct);
        audit.Record(AuditActions.PluginInstanceCreated).For("plugin_instance", instance.Id, slug).With("by_plugin", caller.PluginId);
        return new InstanceRef(instance.Id, slug, Created: true);
    }
}
