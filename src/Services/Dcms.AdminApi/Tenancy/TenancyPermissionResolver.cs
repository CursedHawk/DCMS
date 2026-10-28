using Dcms.Shared.Caching;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Security.Authorization;
using Microsoft.EntityFrameworkCore;

using Dcms.Shared.Data;
namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Resolves a user's effective permissions in a tenant from tenant_role_permissions, cached in
/// Redis (perm:{environmentId}:{userId}, 5 min). Invalidated on role changes and
/// membership.changed events.
///
/// <para>Roles are per environment (ADR 0028), so the answer is for the environment in force for
/// the tenant -- the request's, or an enclosing <see cref="RlsScope"/> block's -- and production
/// otherwise. Production's id is the tenant's, so its key is the one it always was.</para>
/// </summary>
public sealed class TenancyPermissionResolver(TenancyDbContext db, ICacheService cache)
    : IPermissionResolver
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public static string CacheKey(Guid environmentId, Guid userId) => $"perm:{environmentId}:{userId}";

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var environmentId = RlsScope.EnvironmentFor(tenantId) ?? tenantId;
        var key = CacheKey(environmentId, userId);
        var cached = await cache.GetAsync<string[]>(key, ct);
        if (cached is not null)
        {
            return cached.ToHashSet(StringComparer.Ordinal);
        }

        // Keyed on the tenant and environment explicitly, not on the ambient ones: the deployment
        // engine asks for another environment's permissions from inside a request, and an answer
        // read through the ambient filter would be the wrong environment's -- cached under this
        // one's key for five minutes.
        string[] permissions;
        using (RlsScope.Environment(tenantId, environmentId))
        {
            var roleIds = await db.MemberRoles.IgnoreTenantFilter()
                .Where(r => r.TenantId == tenantId && r.EnvironmentId == environmentId
                            && db.Memberships.IgnoreTenantFilter().Any(m => m.Id == r.MembershipId && m.TenantId == tenantId && m.UserId == userId))
                .Select(r => r.TenantRoleId)
                .ToListAsync(ct);

            permissions = roleIds.Count == 0
                ? []
                : await db.TenantRolePermissions.IgnoreTenantFilter()
                    .Where(p => p.TenantId == tenantId && p.EnvironmentId == environmentId && roleIds.Contains(p.TenantRoleId))
                    .Select(p => p.Permission)
                    .Distinct()
                    .ToArrayAsync(ct);
        }

        await cache.SetAsync(key, permissions, Ttl, ct);
        return permissions.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Drops the user's cached permissions in every environment of the tenant.</summary>
    public async Task InvalidateAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        foreach (var environmentId in await EnvironmentIdsAsync(db, tenantId, ct))
        {
            await cache.RemoveAsync(CacheKey(environmentId, userId), ct);
        }
    }

    /// <summary>The tenant's environments, production first; production alone for a tenant that has gone.</summary>
    public static async Task<List<Guid>> EnvironmentIdsAsync(TenancyDbContext db, Guid tenantId, CancellationToken ct)
    {
        using var rls = RlsScope.Tenant(tenantId);
        var ids = await db.Environments.IgnoreTenantFilter().AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Id != tenantId)
            .Select(e => e.Id)
            .ToListAsync(ct);
        ids.Insert(0, tenantId);
        return ids;
    }
}
