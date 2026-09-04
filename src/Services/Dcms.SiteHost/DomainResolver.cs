using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Dcms.SiteHost;

public sealed record SiteRoute(Guid TenantId, string TenantSlug, Guid SiteId, string ArtifactPrefix);

/// <summary>
/// Resolves an incoming Host header to the tenant + active build artifacts.
/// Only verified domains linked to a site with a succeeded build resolve.
/// Cached in-memory (short TTL); invalidated on site.published / domain events.
/// </summary>
public sealed class DomainResolver(IServiceProvider services, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public async Task<SiteRoute?> ResolveAsync(string host, CancellationToken ct)
    {
        var hostname = Normalize(host);
        if (cache.TryGetValue<SiteRoute?>(Key(hostname), out var cached))
        {
            return cached;
        }

        var route = await LoadAsync(hostname, ct);
        cache.Set(Key(hostname), route, Ttl);
        return route;
    }

    /// <summary>
    /// TLS gate for the edge: a hostname may be issued a certificate only if it is
    /// a verified domain linked to a site (a published build is not required — the
    /// cert can be minted ahead of the first publish).
    /// </summary>
    public async Task<bool> IsTlsAllowedAsync(string host, CancellationToken ct)
    {
        var hostname = Normalize(host);
        using var scope = services.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenancy.Domains.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(d => d.Hostname == hostname && d.VerifiedAt != null && d.SiteId != null, ct);
    }

    /// <summary>
    /// Every hostname that may hold a certificate right now.
    ///
    /// <para>The edge asks for this so it can issue ahead of the first visitor instead of inside
    /// their TLS handshake. That distinction is not a nicety: a full ACME order routinely takes
    /// longer than a handshake can wait, so a domain whose certificate is only ever attempted
    /// on demand fails the first visit, records a failure, backs off, and fails the next one
    /// further away — which is what an empty certificate store looks like from a browser.</para>
    ///
    /// <para>Answered here rather than read from tenancy by the edge, for the same reason
    /// <c>IsTlsAllowedAsync</c> is: the edge is the most exposed process on the platform and
    /// holds no grant on <c>tenancy.domains</c>. This is the same query it already trusts us
    /// for, returning the set instead of one answer.</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> TlsAllowedHostnamesAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenancy.Domains.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.VerifiedAt != null && d.SiteId != null)
            .Select(d => d.Hostname)
            .OrderBy(h => h)
            .ToListAsync(ct);
    }

    public void Invalidate(string host) => cache.Remove(Key(Normalize(host)));

    public void InvalidateAll()
    {
        // MemoryCache has no clear-all; entries expire within the TTL. Site-level
        // invalidation is keyed by host, handled by Invalidate where the host is known.
    }

    private async Task<SiteRoute?> LoadAsync(string hostname, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var sites = scope.ServiceProvider.GetRequiredService<SitesDbContext>();

        var domain = await tenancy.Domains.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(d => d.Hostname == hostname && d.VerifiedAt != null && d.SiteId != null, ct);
        if (domain?.SiteId is not { } siteId)
        {
            return null;
        }

        var site = await sites.Sites.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == siteId, ct);
        if (site?.ActiveBuildId is not { } buildId)
        {
            return null;
        }

        var build = await sites.Builds.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == buildId, ct);
        if (build is null)
        {
            return null;
        }

        var tenant = await tenancy.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == domain.TenantId.ToString(), ct);
        if (tenant is null)
        {
            return null;
        }

        // The delivery-plane half of suspension, and the half that actually matters: stopping
        // an operator's admin access while the public site keeps serving would suspend nothing
        // anyone can see. Enforced here rather than at the endpoint because every route to a
        // tenant's content goes through this resolver.
        //
        // Treated as "no route" rather than a distinct status: the caller is an anonymous
        // visitor on the public internet, and whether a hostname is unclaimed, unpublished or
        // suspended is not their business. TenantStatusInvalidator drops the cached entry the
        // moment the status changes, so this does not wait out the five-minute TTL.
        if (tenant.Status == TenantStatus.Suspended)
        {
            return null;
        }

        return new SiteRoute(domain.TenantId, tenant.Identifier, siteId, build.ArtifactPrefix);
    }

    private static string Normalize(string host)
    {
        var h = host.Split(':')[0].Trim().ToLowerInvariant();
        return h;
    }

    private static string Key(string hostname) => $"siteroute:{hostname}";
}
