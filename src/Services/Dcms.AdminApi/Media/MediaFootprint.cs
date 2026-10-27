using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Media;

/// <summary>
/// What a tenant's media library occupies and may occupy. Database only, so the usage snapshot
/// can read it on a cluster whose object storage is not configured.
/// </summary>
public static class MediaFootprint
{
    /// <summary>Originals and renditions: their sum is what the storage cap is measured against.</summary>
    public static async Task<(long Originals, long Renditions)> OfAsync(MediaDbContext db, Guid tenantId, CancellationToken ct)
    {
        // rls: request tenant. Every caller ingests into the tenant the request is already
        // scoped to (a plugin's Caller.TenantId is that same tenant), so the explicit TenantId
        // filter and the database policy agree; IgnoreQueryFilters only drops the ambient filter
        // for the Meta-sync-style caller that names its tenant. tenancy.tenants carries no policy.
        var originals = await db.Assets.IgnoreTenantFilter()
            .Where(a => a.TenantId == tenantId && a.OriginalDeletedAt == null)
            .SumAsync(a => (long?)a.SizeBytes, ct) ?? 0;
        var renditions = await db.Variants.IgnoreTenantFilter()
            .Where(v => v.TenantId == tenantId).SumAsync(v => (long?)v.SizeBytes, ct) ?? 0;
        return (originals, renditions);
    }

    /// <summary>
    /// The cap: the workspace root's, because a workspace's environments share one (ADR 0026) --
    /// otherwise every environment added would multiply it.
    /// </summary>
    public static async Task<long> QuotaAsync(TenancyDbContext tenancy, Guid tenantId, CancellationToken ct)
    {
        var row = await tenancy.Tenants.IgnoreTenantFilter()
            .Where(t => t.Id == tenantId.ToString())
            .Select(t => new { t.StorageQuotaBytes, t.WorkspaceId })
            .FirstOrDefaultAsync(ct);
        if (row is null) return Tenant.DefaultStorageQuotaBytes;
        return row.WorkspaceId is { } root && root != tenantId
            ? await QuotaAsync(tenancy, root, ct)
            : row.StorageQuotaBytes;
    }

    /// <summary>What the cap is measured against: the media of every environment of the workspace.</summary>
    public static async Task<long> WorkspaceUsedAsync(
        MediaDbContext media, TenancyDbContext tenancy, Guid tenantId, CancellationToken ct)
    {
        var workspaceId = await tenancy.Tenants.IgnoreTenantFilter()
            .Where(t => t.Id == tenantId.ToString())
            .Select(t => t.WorkspaceId)
            .FirstOrDefaultAsync(ct);
        var tenantIds = workspaceId is { } root
            ? (await tenancy.Tenants.IgnoreTenantFilter().Where(t => t.WorkspaceId == root).Select(t => t.Id).ToListAsync(ct))
                .Select(Guid.Parse).ToList()
            : [tenantId];

        long used = 0;
        foreach (var id in tenantIds)
        {
            // rls: each environment's own rows, read as that environment -- a sum, nothing returned.
            using var rls = RlsScope.Tenant(id);
            var (originals, renditions) = await OfAsync(media, id, ct);
            used += originals + renditions;
        }
        return used;
    }
}
