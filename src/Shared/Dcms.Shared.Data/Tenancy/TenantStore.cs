using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// Finbuckle store backed by the tenancy.tenants table. Read paths are used for
/// resolution; writes go through the management endpoints, so add/update/remove
/// here just proxy the context for completeness.
/// </summary>
public sealed class TenantStore(TenancyDbContext db) : IMultiTenantStore<Tenant>
{
    public async Task<Tenant?> GetByIdentifierAsync(string identifier)
        => await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Identifier == identifier);

    public async Task<Tenant?> GetAsync(string id)
        => await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);

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
