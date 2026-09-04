using Dcms.Shared.Caching;
using Dcms.Shared.Data.Platform;
using Dcms.Shared.Security.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Dcms.PlatformApi.Authz;

/// <summary>
/// Resolves the platform-console permissions carried by a set of global roles, cached in Redis.
///
/// <para><b>The cache key is the roles, not the user.</b> Two SuperAdmins have identical
/// permissions by construction — a platform permission is held by a role — so keying on the
/// user id would store the same set once per operator and invalidate N entries on one grant
/// change. Keying on the sorted role list means the whole platform shares a handful of
/// entries, and a grant change invalidates by bumping one generation counter rather than by
/// enumerating who is affected.</para>
///
/// <para>The five-minute TTL matches <c>TenancyPermissionResolver</c>'s deliberately. It is
/// the window in which a revoked permission still works, and the console's most dangerous
/// keys (<c>platform:logs:purge</c>, <c>platform:users:roles</c>) are inside it — so a
/// revocation that has to take effect now is a revocation of the global ROLE in identity,
/// which drops out of the access token at its next 10-minute refresh and is not cached here
/// at all.</para>
/// </summary>
public sealed class PlatformPermissionResolver(
    PlatformDbContext db,
    ICacheService cache,
    ILogger<PlatformPermissionResolver> logger)
    : IPlatformPermissionResolver
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>Bumped by <see cref="InvalidateAsync"/>; folded into every cache key.</summary>
    private const string GenerationKey = "pperm:gen";

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(
        IReadOnlyCollection<string> roles, CancellationToken ct = default)
    {
        if (roles.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        // Sorted and de-duplicated so ["Support","SuperAdmin"] and ["SuperAdmin","Support"]
        // are one cache entry rather than two.
        var normalized = roles.Distinct(StringComparer.Ordinal)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        var generation = await GenerationAsync(ct);
        var key = $"pperm:{generation}:{string.Join('|', normalized)}";

        var cached = await cache.GetAsync<string[]>(key, ct);
        if (cached is not null)
        {
            return new HashSet<string>(cached, StringComparer.Ordinal);
        }

        var permissions = await db.RolePermissions
            .AsNoTracking()
            .Where(rp => normalized.Contains(rp.RoleName))
            .Select(rp => rp.Permission)
            .Distinct()
            .ToArrayAsync(ct);

        await cache.SetAsync(key, permissions, Ttl, ct);
        return new HashSet<string>(permissions, StringComparer.Ordinal);
    }

    /// <summary>
    /// Invalidates every cached permission set by moving the generation forward.
    ///
    /// <para>A generation bump rather than a key sweep because there is no way to enumerate
    /// the role combinations in play — the key space is every subset of the global roles that
    /// some live token happens to carry. Redis SCAN over a shared cache to delete a handful of
    /// keys is the kind of operation that is fine until the day it is not.</para>
    /// </summary>
    public async Task InvalidateAsync(CancellationToken ct = default)
    {
        var generation = await cache.IncrementAsync(GenerationKey, ct);
        logger.LogInformation("Platform permission cache invalidated; generation is now {Generation}.", generation);
    }

    private async Task<long> GenerationAsync(CancellationToken ct)
    {
        var current = await cache.GetAsync<long?>(GenerationKey, ct);
        return current ?? 0;
    }
}
