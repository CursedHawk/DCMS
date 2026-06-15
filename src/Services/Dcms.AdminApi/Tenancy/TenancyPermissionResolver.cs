using Dcms.Shared.Caching;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Security.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Resolves a user's effective permissions in a tenant from tenant_role_permissions,
/// cached in Redis (perm:{tenantId}:{userId}, 5 min). Invalidated on role changes
/// and membership.changed events.
/// </summary>
public sealed class TenancyPermissionResolver(TenancyDbContext db, ICacheService cache)
    : IPermissionResolver
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public static string CacheKey(Guid tenantId, Guid userId) => $"perm:{tenantId}:{userId}";

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var key = CacheKey(tenantId, userId);
        var cached = await cache.GetAsync<string[]>(key, ct);
        if (cached is not null)
        {
            return cached.ToHashSet(StringComparer.Ordinal);
        }

        // Tenant-scoped query filters restrict these to the current tenant, which
        // equals tenantId for the request being authorized.
        var roleIds = await db.Memberships
            .Where(m => m.UserId == userId)
            .SelectMany(m => m.Roles.Select(r => r.TenantRoleId))
            .ToListAsync(ct);

        var permissions = roleIds.Count == 0
            ? []
            : await db.TenantRolePermissions
                .Where(p => roleIds.Contains(p.TenantRoleId))
                .Select(p => p.Permission)
                .Distinct()
                .ToArrayAsync(ct);

        await cache.SetAsync(key, permissions, Ttl, ct);
        return permissions.ToHashSet(StringComparer.Ordinal);
    }

    public Task InvalidateAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
        => cache.RemoveAsync(CacheKey(tenantId, userId), ct);
}
