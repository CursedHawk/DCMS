using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

public static class DomainEndpoints
{
    public const string TxtPrefix = "_dcms-verify";

    /// <summary>Zone the platform owns; provisioned subdomains live under it.</summary>
    public const string DefaultManagedZone = "dcms.highgeek.eu";

    public static IEndpointRouteBuilder MapDomainEndpoints(this IEndpointRouteBuilder app)
    {
        // The site name is resolved here rather than left to the SPA: listing sites
        // needs SiteEdit, and someone with only DomainsManage must still be able to
        // see which site each of their domains serves.
        app.MapGet("/api/admin/domains", async (
            TenancyDbContext db, SitesDbContext sites, CancellationToken ct) =>
        {
            var domains = await db.Domains
                .OrderBy(d => d.Hostname)
                .Select(d => new
                {
                    id = d.Id,
                    hostname = d.Hostname,
                    verified = d.VerifiedAt != null,
                    isPrimary = d.IsPrimary,
                    managed = d.VerificationToken == "dcms-managed",
                    siteId = d.SiteId,
                    txtRecord = $"{TxtPrefix}.{d.Hostname}",
                    txtValue = d.VerificationToken,
                })
                .ToListAsync(ct);

            var siteIds = domains.Where(d => d.siteId != null).Select(d => d.siteId!.Value).Distinct().ToList();
            var names = await sites.Sites
                .Where(s => siteIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

            return Results.Ok(domains.Select(d => new
            {
                d.id,
                d.hostname,
                d.verified,
                d.isPrimary,
                d.managed,
                d.siteId,
                // Null when the linked site has been deleted out from under the
                // domain — the SPA shows that as "unlinked" rather than a blank row.
                siteName = d.siteId is { } id && names.TryGetValue(id, out var n) ? n : null,
                d.txtRecord,
                d.txtValue,
            }));
        }).RequirePermission(PlatformPermissions.DomainsManage);

        app.MapPost("/api/admin/domains", async (
            AddDomainRequest body, TenancyDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var hostname = body.Hostname.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(hostname) || !hostname.Contains('.'))
            {
                return Results.BadRequest(new { error = "Invalid hostname." });
            }
            if (await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.Hostname == hostname, ct))
            {
                return Results.Conflict(new { error = "Hostname already registered." });
            }

            var domain = new Domain
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId!.Value,
                Hostname = hostname,
                VerificationToken = "dcms-verify=" + Guid.NewGuid().ToString("N"),
            };
            db.Domains.Add(domain);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/admin/domains/{domain.Id}", new
            {
                id = domain.Id,
                hostname = domain.Hostname,
                txtRecord = $"{TxtPrefix}.{domain.Hostname}",
                txtValue = domain.VerificationToken,
            });
        }).RequirePermission(PlatformPermissions.DomainsManage).WithAudit(AuditActions.DomainAdded, "domain");

        // Provision a managed subdomain under the platform-owned zone
        // ({domainId}.dcms.highgeek.eu). The platform controls the zone (wildcard
        // DNS + on-demand TLS), so there is no TXT challenge — it's verified on
        // creation and ready to link to a site immediately.
        app.MapPost("/api/admin/domains/provisioned", async (
            TenancyDbContext db, ITenantContext tenant, IConfiguration config,
            IEventPublisher events, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.BadRequest(new { error = "Select a tenant first." });
            }

            var zone = (config["Domains:ManagedZone"] ?? DefaultManagedZone).Trim().Trim('.').ToLowerInvariant();
            var domainId = Guid.NewGuid();
            var hostname = $"{domainId:N}.{zone}";

            var domain = new Domain
            {
                Id = domainId,
                TenantId = tenantId,
                Hostname = hostname,
                VerificationToken = "dcms-managed",
                VerifiedAt = DateTimeOffset.UtcNow,
            };
            db.Domains.Add(domain);
            await db.SaveChangesAsync(ct);

            await events.PublishAsync(Subjects.TenantDomainVerified,
                new TenantDomainVerified(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, domain.Id, domain.Hostname), ct);

            return Results.Created($"/api/admin/domains/{domain.Id}", new
            {
                id = domain.Id,
                hostname = domain.Hostname,
                verified = true,
                isPrimary = domain.IsPrimary,
            });
        }).RequirePermission(PlatformPermissions.DomainsManage).WithAudit(AuditActions.DomainProvisioned, "domain");

        app.MapPost("/api/admin/domains/{id:guid}/verify", async (
            Guid id, TenancyDbContext db, IDnsTxtLookup dns, IConfiguration config,
            IEventPublisher events, ITenantContext tenant, CancellationToken ct) =>
        {
            var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (domain is null)
            {
                return Results.NotFound();
            }
            if (domain.IsVerified)
            {
                return Results.Ok(new { verified = true });
            }

            var verified = config.GetValue("Domains:AutoVerify", false);
            if (!verified)
            {
                var records = await dns.GetTxtRecordsAsync($"{TxtPrefix}.{domain.Hostname}", ct);
                verified = records.Any(r => string.Equals(r, domain.VerificationToken, StringComparison.Ordinal));
            }

            if (!verified)
            {
                return Results.BadRequest(new { verified = false, error = "TXT record not found or mismatched." });
            }

            domain.VerifiedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await events.PublishAsync(Subjects.TenantDomainVerified,
                new TenantDomainVerified(Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId!.Value, domain.Id, domain.Hostname), ct);

            return Results.Ok(new { verified = true, isPrimary = domain.IsPrimary });
        }).RequirePermission(PlatformPermissions.DomainsManage).WithAudit(AuditActions.DomainVerified, "domain");

        // Link a verified domain to a site (or unlink it with a null siteId) so
        // site-host serves it there. Lives beside the other domain routes because
        // it is where primary-domain bookkeeping happens.
        app.MapPost("/api/admin/domains/{id:guid}/site", async (
            Guid id, LinkSiteRequest body, TenancyDbContext db, SitesDbContext sites, CancellationToken ct) =>
        {
            var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (domain is null)
            {
                return Results.NotFound();
            }
            if (body.SiteId is { } target && !await sites.Sites.AnyAsync(s => s.Id == target, ct))
            {
                return Results.BadRequest(new { error = "Unknown site." });
            }

            var previousSiteId = domain.SiteId;
            domain.SiteId = body.SiteId;

            // "Primary" is the canonical hostname *of a site*, so a domain that moves
            // to another site (or is unlinked) cannot carry the flag with it.
            if (previousSiteId != body.SiteId)
            {
                domain.IsPrimary = false;
            }
            // First domain on a site becomes its primary: a site with domains but no
            // canonical one would leave the OpenAPI server list arbitrarily ordered.
            if (body.SiteId is { } siteId &&
                !await db.Domains.AnyAsync(d => d.SiteId == siteId && d.IsPrimary && d.Id != domain.Id, ct))
            {
                domain.IsPrimary = true;
            }

            await db.SaveChangesAsync(ct);
            if (previousSiteId is { } orphaned && orphaned != body.SiteId)
            {
                await PromotePrimaryAsync(db, orphaned, ct);
            }
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.DomainsManage).WithAudit(AuditActions.DomainLinked, "domain");

        // Make this the canonical hostname for the site it serves. Exclusive within
        // the site, not the tenant — a tenant with several sites has one primary each.
        app.MapPost("/api/admin/domains/{id:guid}/primary", async (
            Guid id, TenancyDbContext db, CancellationToken ct) =>
        {
            var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (domain is null)
            {
                return Results.NotFound();
            }
            if (domain.SiteId is not { } siteId)
            {
                return Results.BadRequest(new { error = "Link the domain to a site before making it primary." });
            }
            if (!domain.IsVerified)
            {
                return Results.BadRequest(new { error = "Verify the domain before making it primary." });
            }

            var siblings = await db.Domains.Where(d => d.SiteId == siteId).ToListAsync(ct);
            foreach (var sibling in siblings)
            {
                sibling.IsPrimary = sibling.Id == domain.Id;
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.DomainsManage).WithAudit(AuditActions.DomainPrimarySet, "domain");

        // Remove a domain. The row is the only record that a hostname belongs to this
        // tenant, so deleting it both stops site-host resolving the host and releases
        // the name for another tenant to claim. site-host caches routes for up to five
        // minutes (see SiteHost/DomainResolver), so the host keeps serving until the
        // entry expires.
        app.MapDelete("/api/admin/domains/{id:guid}", async (
            Guid id, TenancyDbContext db, CancellationToken ct) =>
        {
            var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (domain is null)
            {
                return Results.NotFound();
            }
            var siteId = domain.SiteId;
            db.Domains.Remove(domain);
            await db.SaveChangesAsync(ct);
            if (siteId is { } orphaned)
            {
                await PromotePrimaryAsync(db, orphaned, ct);
            }
            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.DomainsManage).WithAudit(AuditActions.DomainRemoved, "domain");

        return app;
    }

    /// <summary>
    /// Ensures a site that still has verified domains has exactly one primary. Called
    /// after the current primary is deleted, unlinked or moved; picks the alphabetically
    /// first verified domain so the choice is stable rather than insertion-ordered.
    /// </summary>
    private static async Task PromotePrimaryAsync(TenancyDbContext db, Guid siteId, CancellationToken ct)
    {
        var remaining = await db.Domains
            .Where(d => d.SiteId == siteId)
            .OrderBy(d => d.Hostname)
            .ToListAsync(ct);
        if (remaining.Count == 0 || remaining.Any(d => d.IsPrimary))
        {
            return;
        }
        var replacement = remaining.FirstOrDefault(d => d.VerifiedAt != null);
        if (replacement is not null)
        {
            replacement.IsPrimary = true;
            await db.SaveChangesAsync(ct);
        }
    }

    private sealed record AddDomainRequest(string Hostname);
    private sealed record LinkSiteRequest(Guid? SiteId);
}
