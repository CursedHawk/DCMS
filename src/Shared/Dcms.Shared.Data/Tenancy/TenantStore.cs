using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// Finbuckle store backed by the tenancy.tenants table. Read paths are used for
/// resolution; writes go through the management endpoints, so add/update/remove
/// here just proxy the context for completeness.
/// <para>
/// Resolution runs on every tenant-scoped request, so hits are cached for
/// <see cref="Ttl"/>: uncached it was one query, connection checkout, RLS set_config and
/// DISCARD ALL per request -- ~680k lookups found by the 2026-09-26 load test. Only hits:
/// a tenant created a moment ago must resolve at once. The cost is that a suspension
/// reaches this process up to 30 s late, well inside the five minutes site-host's route
/// cache already allows it (TenancyEndpoints.SetTenantStatusAsync).
/// </para>
/// </summary>
public sealed class TenantStore(TenancyDbContext db, IMemoryCache cache) : IMultiTenantStore<Tenant>
{
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public Task<Tenant?> GetByIdentifierAsync(string identifier)
        => CachedAsync($"tenant:identifier:{identifier}",
            () => db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Identifier == identifier));

    public Task<Tenant?> GetAsync(string id)
        => CachedAsync($"tenant:id:{id}", () => db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id));

    /// <summary>Drops a tenant this process has cached, so a status change it made applies to its next request.</summary>
    public void Evict(Tenant tenant)
    {
        cache.Remove($"tenant:identifier:{tenant.Identifier}");
        cache.Remove($"tenant:id:{tenant.Id}");
    }

    private async Task<Tenant?> CachedAsync(string key, Func<Task<Tenant?>> load)
    {
        if (cache.TryGetValue(key, out Tenant? hit))
        {
            return hit;
        }
        var tenant = await load();
        if (tenant is not null)
        {
            cache.Set(key, tenant, Ttl);
        }
        return tenant;
    }

    public async Task<IEnumerable<Tenant>> GetAllAsync()
        => await db.Tenants.AsNoTracking().OrderBy(t => t.Identifier).ToListAsync();

    public async Task<IEnumerable<Tenant>> GetAllAsync(int take, int skip)
        => await db.Tenants.AsNoTracking().OrderBy(t => t.Identifier).Skip(skip).Take(take).ToListAsync();

    public async Task<bool> AddAsync(Tenant tenantInfo)
    {
        db.Tenants.Add(tenantInfo);
        return await db.SaveChangesAsync() > 0;
    }

    public async Task<bool> UpdateAsync(Tenant tenantInfo)
    {
        db.Tenants.Update(tenantInfo);
        return await db.SaveChangesAsync() > 0;
    }

    public async Task<bool> RemoveAsync(string identifier)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Identifier == identifier);
        if (tenant is null)
        {
            return false;
        }
        db.Tenants.Remove(tenant);
        return await db.SaveChangesAsync() > 0;
    }
}
