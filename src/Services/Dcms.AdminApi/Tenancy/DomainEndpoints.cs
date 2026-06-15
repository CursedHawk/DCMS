using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

public static class DomainEndpoints
{
    public const string TxtPrefix = "_dcms-verify";

    public static IEndpointRouteBuilder MapDomainEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/domains", async (TenancyDbContext db, CancellationToken ct) =>
        {
            var domains = await db.Domains
                .Select(d => new
                {
                    id = d.Id,
                    hostname = d.Hostname,
                    verified = d.VerifiedAt != null,
                    isPrimary = d.IsPrimary,
                    txtRecord = $"{TxtPrefix}.{d.Hostname}",
                    txtValue = d.VerificationToken,
                })
                .ToListAsync(ct);
            return Results.Ok(domains);
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
        }).RequirePermission(PlatformPermissions.DomainsManage);

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
            if (!await db.Domains.AnyAsync(d => d.IsPrimary, ct))
            {
                domain.IsPrimary = true;
            }
            await db.SaveChangesAsync(ct);

            await events.PublishAsync(Subjects.TenantDomainVerified,
                new TenantDomainVerified(Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId!.Value, domain.Id, domain.Hostname), ct);

            return Results.Ok(new { verified = true, isPrimary = domain.IsPrimary });
        }).RequirePermission(PlatformPermissions.DomainsManage);

        return app;
    }

    private sealed record AddDomainRequest(string Hostname);
}
