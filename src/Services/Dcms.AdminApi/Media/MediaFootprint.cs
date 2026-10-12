using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

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
        var originals = await db.Assets.IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.OriginalDeletedAt == null)
            .SumAsync(a => (long?)a.SizeBytes, ct) ?? 0;
        var renditions = await db.Variants.IgnoreQueryFilters()
            .Where(v => v.TenantId == tenantId).SumAsync(v => (long?)v.SizeBytes, ct) ?? 0;
        return (originals, renditions);
    }

    public static async Task<long> QuotaAsync(TenancyDbContext tenancy, Guid tenantId, CancellationToken ct) =>
        await tenancy.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == tenantId.ToString())
            .Select(t => (long?)t.StorageQuotaBytes)
            .FirstOrDefaultAsync(ct) ?? Tenant.DefaultStorageQuotaBytes;
}
