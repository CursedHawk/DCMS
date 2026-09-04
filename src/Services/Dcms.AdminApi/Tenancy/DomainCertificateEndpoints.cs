using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Dcms.Shared.Security;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// The certificate half of a domain, which the product could not see at all until the edge moved
/// inside it.
///
/// <para>Caddy's <c>/data</c> was the only record that a hostname had a certificate, when it
/// expired, or why issuance had failed — so the admin UI could say "verified" and nothing else,
/// and "why is my domain showing a browser warning" had no answer anybody could read. These
/// endpoints exist because <c>edge.certificates</c> is now an ordinary table.</para>
///
/// <para><b>Reads and writes the edge's schema directly</b> rather than calling the edge over
/// HTTP. admin-api runs as the database owner and already migrates that schema; adding an
/// internal HTTP hop would buy a second failure mode and no boundary. The edge is told to act
/// through the event bus, which is how it already learns about domains.</para>
/// </summary>
public static class DomainCertificateEndpoints
{
    public static IEndpointRouteBuilder MapDomainCertificateEndpoints(this IEndpointRouteBuilder app)
    {
        // Certificate state for every domain this tenant owns, keyed by hostname.
        //
        // A separate call from the domains list rather than a join inside it: the domains list is
        // what the page needs to render at all, and it must not start failing because the edge
        // schema is unreachable. This one degrading to "unknown" is a worse page, not a broken
        // one.
        app.MapGet("/api/admin/domains/certificates", async (
            TenancyDbContext db, EdgeDbContext edge, CancellationToken ct) =>
        {
            var hostnames = await db.Domains.Select(d => d.Hostname).ToListAsync(ct);
            var rows = await edge.Certificates.AsNoTracking()
                .Where(c => hostnames.Contains(c.Hostname))
                .ToListAsync(ct);

            var now = DateTimeOffset.UtcNow;
            return Results.Ok(rows.Select(c => new
            {
                hostname = c.Hostname,
                source = c.Source == CertificateSource.Custom ? "custom" : "dcms",
                issuer = c.Issuer,
                notBefore = c.NotBefore,
                notAfter = c.NotAfter,
                renewedAt = c.RenewedAt,
                reissueRequested = c.ReissueRequestedAt != null,
                // The CA's own sentence, which is the half Caddy logged once and discarded. It
                // is usually the only thing that says which DNS record is wrong.
                lastError = c.LastError,
                lastAttemptAt = c.LastAttemptAt,
                consecutiveFailures = c.ConsecutiveFailures,
                expired = c.NotAfter <= now,
                daysRemaining = (int)Math.Floor((c.NotAfter - now).TotalDays),
            }));
        }).RequirePermission(PlatformPermissions.DomainsManage);

        // Ask for a fresh certificate before the current one is due.
        //
        // Writes a flag AND publishes an event, which is not belt-and-braces for its own sake:
        // the event makes it immediate, the flag makes it certain. The hourly renewal sweep
        // picks up anything flagged, so a dropped message costs an hour rather than the reissue,
        // and the operator does not have to know which of the two happened.
        app.MapPost("/api/admin/domains/{id:guid}/certificate/reissue", async (
            Guid id, TenancyDbContext db, EdgeDbContext edge, IEventPublisher events,
            ITenantContext tenant, CancellationToken ct) =>
        {
            if (await db.Domains.FirstOrDefaultAsync(d => d.Id == id, ct) is not { } domain)
            {
                return Results.NotFound();
            }
            if (!domain.IsVerified)
            {
                return Results.BadRequest(new { error = "Verify the domain before requesting a certificate." });
            }

            var row = await edge.Certificates.FirstOrDefaultAsync(c => c.Hostname == domain.Hostname, ct);
            if (row is { Source: CertificateSource.Custom })
            {
                // The platform holds no authority to reissue somebody else's certificate, and
                // quietly replacing it with a Let's Encrypt one would undo a deliberate choice.
                return Results.BadRequest(new
                {
                    error = "This domain uses an uploaded certificate. Upload a replacement, or remove it to return to DCMS-managed certificates.",
                });
            }

            if (row is not null)
            {
                row.ReissueRequestedAt = DateTimeOffset.UtcNow;
                // Cleared so a domain that has been failing is retried now rather than sitting
                // out the backoff the failures earned it. The operator asking IS the new
                // information -- usually that they have just fixed the DNS record.
                row.ConsecutiveFailures = 0;
                row.LastError = null;
                await edge.SaveChangesAsync(ct);
            }

            await events.PublishAsync(Subjects.TenantDomainVerified,
                new TenantDomainVerified(Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId!.Value, domain.Id, domain.Hostname), ct);

            return Results.Accepted();
        }).RequirePermission(PlatformPermissions.DomainsManage)
          .WithAudit(AuditActions.DomainCertificateReissued, "domain");

        // Upload your own certificate for a domain you have verified.
        app.MapPut("/api/admin/domains/{id:guid}/certificate", async (
            Guid id, CertificateUpload.Request body, TenancyDbContext db, EdgeDbContext edge,
            ITransitEncryptor transit, IEventPublisher events, ITenantContext tenant,
            CancellationToken ct) =>
        {
            if (await db.Domains.FirstOrDefaultAsync(d => d.Id == id, ct) is not { } domain)
            {
                return Results.NotFound();
            }
            if (!domain.IsVerified)
            {
                // Ownership of the hostname is what this is authorized by. Without verification
                // a tenant could install a certificate for a name that is not theirs and have
                // the edge serve it.
                return Results.BadRequest(new { error = "Verify the domain before uploading a certificate." });
            }

            if (CertificateUpload.Validate(body, domain.Hostname, DateTimeOffset.UtcNow) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            var leaf = CertificateUpload.ParseLeaf(body.PemChain)!;
            var row = await edge.Certificates.FirstOrDefaultAsync(c => c.Hostname == domain.Hostname, ct);
            if (row is null)
            {
                row = new EdgeCertificate { Hostname = domain.Hostname };
                edge.Certificates.Add(row);
            }

            row.PemChain = body.PemChain;
            // Encrypted before it touches the database, exactly as an issued key is. admin-api
            // holds encrypt on this Transit key and NOT decrypt: only the edge reads one back,
            // at handshake time, so a compromise of the admin plane cannot turn stored
            // ciphertext into a key that impersonates a tenant's site.
            row.EncryptedPrivateKey = await transit.EncryptAsync(
                VaultTransitServiceCollectionExtensions.TlsKeysKey,
                System.Text.Encoding.UTF8.GetBytes(body.PrivateKeyPem), ct);
            row.NotBefore = leaf.NotBefore;
            row.NotAfter = leaf.NotAfter;
            row.Issuer = leaf.Issuer;
            row.Source = CertificateSource.Custom;
            row.RenewedAt = DateTimeOffset.UtcNow;
            row.LastAttemptAt = DateTimeOffset.UtcNow;
            row.LastError = null;
            row.ConsecutiveFailures = 0;
            row.ReissueRequestedAt = null;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await edge.SaveChangesAsync(ct);

            // The edge caches a loaded certificate until it expires, so without this it would go
            // on serving the one that was just replaced for as long as that one had left.
            await events.PublishAsync(Subjects.TenantDomainVerified,
                new TenantDomainVerified(Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId!.Value, domain.Id, domain.Hostname), ct);

            return Results.Ok(new { hostname = domain.Hostname, notAfter = row.NotAfter, issuer = row.Issuer });
        }).RequirePermission(PlatformPermissions.DomainsManage)
          .WithAudit(AuditActions.DomainCertificateUploaded, "domain");

        // Drop an uploaded certificate and go back to DCMS-managed issuance.
        app.MapDelete("/api/admin/domains/{id:guid}/certificate", async (
            Guid id, TenancyDbContext db, EdgeDbContext edge, IEventPublisher events,
            ITenantContext tenant, CancellationToken ct) =>
        {
            if (await db.Domains.FirstOrDefaultAsync(d => d.Id == id, ct) is not { } domain)
            {
                return Results.NotFound();
            }

            var row = await edge.Certificates.FirstOrDefaultAsync(c => c.Hostname == domain.Hostname, ct);
            if (row is null || row.Source != CertificateSource.Custom)
            {
                return Results.BadRequest(new { error = "This domain has no uploaded certificate." });
            }

            // Removed rather than marked, so the next handshake finds nothing and the ordinary
            // issuance path runs. There is a gap between this and the new certificate arriving;
            // the event below closes it in the usual case, and on-demand issuance closes it in
            // the unusual one.
            edge.Certificates.Remove(row);
            await edge.SaveChangesAsync(ct);

            await events.PublishAsync(Subjects.TenantDomainVerified,
                new TenantDomainVerified(Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId!.Value, domain.Id, domain.Hostname), ct);

            return Results.NoContent();
        }).RequirePermission(PlatformPermissions.DomainsManage)
          .WithAudit(AuditActions.DomainCertificateRemoved, "domain");

        return app;
    }

}
