using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.PluginSdk.Runtime.Contracts;

/// <summary>
/// <see cref="IPluginInstanceStore"/> over <c>plugins.plugin_instances</c>. Scoped by the tenant it
/// is given, in both the predicate and the RLS GUC, rather than by the ambient request tenant —
/// which a job or event handler does not have.
/// </summary>
public sealed class CmsPluginInstanceStore(CmsDbContext db, PluginInstanceCache cache) : IPluginInstanceStore
{
    public async Task<IReadOnlyList<PluginInstanceContext>> ListEnabledAsync(Guid tenantId, CancellationToken ct)
        => (await cache.EnabledAsync(tenantId, db, ct)).Contexts;

    public async Task<PluginInstanceContext?> FindAnyAsync(Guid tenantId, string slug, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(tenantId);
        var row = await db.PluginInstances.IgnoreTenantFilter().AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Slug == slug, ct);
        return row is null ? null : PluginInstanceCache.ToContext(row);
    }
}
