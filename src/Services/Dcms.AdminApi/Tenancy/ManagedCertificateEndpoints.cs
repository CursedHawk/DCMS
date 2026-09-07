using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// The certificates DCMS holds for its <b>own</b> domains, managed from the platform console.
///
/// <para>Until this existed the platform's hostnames were compiled into
/// <c>EdgeOptions.PlatformHostnames</c>, so keeping a new domain renewed meant a code change and
/// a deploy. These endpoints make it a row: a superadmin says which names the platform should
/// hold TLS for — wildcards included — and the edge keeps them renewed. See ADR 0011.</para>
///
/// <para><b>Why here and not platform-api.</b> admin-api runs as the database owner, already
/// migrates the <c>edge</c> schema and already serves <c>DomainCertificateEndpoints</c> over the
/// same tables; platform-api holds no grant on that schema at all. The platform console already
/// calls admin-api for tenancy and the audit log, so this adds a page to an existing client
/// rather than a schema to a service that had no business with it.</para>
///
/// <para><b>SuperAdmin, checked imperatively</b>, following <c>AuditEndpoints</c>: the tenant
/// permission model does not reach platform-wide state, and a tenant admin holding
/// <c>domains:manage</c> must not thereby decide which names the platform itself serves.</para>
/// </summary>
public static class ManagedCertificateEndpoints
{
    /// <summary>
    /// Let's Encrypt allows 100 identifiers on one certificate. Refused here rather than by the
    /// CA, because being refused here costs nothing and being refused there costs a failed order.
    /// </summary>
    private const int MaxIdentifiers = 100;

    public sealed record Request(string Name, string[] Identifiers, bool Enabled);

    public static IEndpointRouteBuilder MapManagedCertificateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/platform/certificates", async (
            EdgeDbContext edge, CurrentUser me, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }

            var managed = await edge.ManagedCertificates.AsNoTracking()
                .OrderBy(m => m.Name)
                .ToListAsync(ct);

            var ids = managed.Select(m => m.Id).ToList();
            var artifacts = await edge.Certificates.AsNoTracking()
                .Where(c => c.ManagedCertificateId != null && ids.Contains(c.ManagedCertificateId!.Value))
                .ToListAsync(ct);

            var now = DateTimeOffset.UtcNow;
            var weekAgo = now.AddDays(-7);

            // One query for every certificate's recent attempts rather than one per row: the
            // console renders the whole table at once, and the remaining-issuance figure is the
            // number an operator checks before pressing anything.
            var recent = await edge.ManagedCertificateAttempts.AsNoTracking()
                .Where(a => ids.Contains(a.ManagedCertificateId) && a.AttemptedAt >= weekAgo)
                .Select(a => new { a.ManagedCertificateId, a.AttemptedAt, a.Succeeded })
                .ToListAsync(ct);

            // The newest attempts, whatever their age, because the reason a certificate does not
            // exist is often older than a week and is the only thing worth reading on the row.
            // Read from the ledger and not from the artifact's LastError: a certificate that has
            // never been issued HAS no artifact, which is exactly when there is something to say.
            var latest = await edge.ManagedCertificateAttempts.AsNoTracking()
                .Where(a => ids.Contains(a.ManagedCertificateId))
                .OrderByDescending(a => a.AttemptedAt)
                .Take(200)
                .Select(a => new { a.ManagedCertificateId, a.AttemptedAt, a.Succeeded, a.ReachedCa, a.Error })
                .ToListAsync(ct);

            return Results.Ok(managed.Select(m =>
            {
                var artifact = artifacts.FirstOrDefault(c => c.ManagedCertificateId == m.Id);
                var issuedThisWeek = recent.Count(a => a.ManagedCertificateId == m.Id && a.Succeeded);
                var lastAttempt = latest.FirstOrDefault(a => a.ManagedCertificateId == m.Id);

                return new
                {
                    id = m.Id,
                    name = m.Name,
                    identifiers = m.Identifiers,
                    enabled = m.Enabled,
                    // Whether it needs DNS-01 and therefore a Cloudflare token. Shown because
                    // "no certificate and no obvious reason" is nearly always this.
                    requiresDns = m.Identifiers.Any(i => i.StartsWith("*.", StringComparison.Ordinal)),
                    issuer = artifact?.Issuer,
                    notBefore = artifact?.NotBefore,
                    notAfter = artifact?.NotAfter,
                    renewedAt = artifact?.RenewedAt,
                    covers = artifact?.SubjectAlternativeNames ?? [],
                    reissueRequested = m.ReissueRequestedAt != null,
                    lastError = lastAttempt is { Succeeded: false } ? lastAttempt.Error : artifact?.LastError,
                    // False when the last failure never became an order — a missing Cloudflare
                    // token rather than the CA refusing. Different sentence, different fix, and
                    // the operator should not go looking at DNS records for the first one.
                    lastErrorReachedCa = lastAttempt is not { Succeeded: false } || lastAttempt.ReachedCa,
                    lastAttemptAt = lastAttempt?.AttemptedAt ?? artifact?.LastAttemptAt,
                    expired = artifact is not null && artifact.NotAfter <= now,
                    daysRemaining = artifact is null
                        ? (int?)null
                        : (int)Math.Floor((artifact.NotAfter - now).TotalDays),
                    issuedThisWeek,
                };
            }));
        }).RequireAuthorization();

        // The attempt history, which is also the rate-limit ledger. Worth surfacing on its own:
        // when the edge refuses to order, this is the evidence for why.
        app.MapGet("/api/admin/platform/certificates/{id:guid}/attempts", async (
            Guid id, EdgeDbContext edge, CurrentUser me, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }

            var attempts = await edge.ManagedCertificateAttempts.AsNoTracking()
                .Where(a => a.ManagedCertificateId == id)
                .OrderByDescending(a => a.AttemptedAt)
                .Take(50)
                .Select(a => new { a.AttemptedAt, a.Succeeded, a.ReachedCa, a.Error, a.Identifiers })
                .ToListAsync(ct);

            return Results.Ok(attempts);
        }).RequireAuthorization();

        app.MapPost("/api/admin/platform/certificates", async (
            Request body, EdgeDbContext edge, CurrentUser me, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }

            if (Validate(body) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            var row = new EdgeManagedCertificate
            {
                Name = body.Name.Trim(),
                Identifiers = Clean(body.Identifiers),
                Enabled = body.Enabled,
            };
            edge.ManagedCertificates.Add(row);
            await edge.SaveChangesAsync(ct);

            return Results.Ok(new { id = row.Id, name = row.Name, identifiers = row.Identifiers });
        }).RequireAuthorization()
          .WithAudit(AuditActions.ManagedCertificateCreated, "managed-certificate");

        app.MapPut("/api/admin/platform/certificates/{id:guid}", async (
            Guid id, Request body, EdgeDbContext edge, CurrentUser me, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }

            if (Validate(body) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            if (await edge.ManagedCertificates.FirstOrDefaultAsync(m => m.Id == id, ct) is not { } row)
            {
                return Results.NotFound();
            }

            row.Name = body.Name.Trim();
            row.Identifiers = Clean(body.Identifiers);
            row.Enabled = body.Enabled;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await edge.SaveChangesAsync(ct);

            // The existing certificate is deliberately left in place and serving. Editing the
            // identifiers does not invalidate the one already issued: it stops matching what is
            // wanted, so the next sweep sees it as due and replaces it -- and until then the
            // platform keeps serving TLS rather than going dark between a save and an order.
            return Results.Ok(new { id = row.Id, name = row.Name, identifiers = row.Identifiers });
        }).RequireAuthorization()
          .WithAudit(AuditActions.ManagedCertificateUpdated, "managed-certificate");

        app.MapDelete("/api/admin/platform/certificates/{id:guid}", async (
            Guid id, EdgeDbContext edge, CurrentUser me, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }

            if (await edge.ManagedCertificates.FirstOrDefaultAsync(m => m.Id == id, ct) is not { } row)
            {
                return Results.NotFound();
            }

            // The artifact is detached rather than deleted, so the certificate goes on serving
            // until it expires. Deleting it here would take TLS away from every name it covers
            // the moment somebody tidied up a list -- for the platform wildcard, that is the
            // whole estate, instantly. Disabling is the reversible form of this; deleting is
            // "stop renewing", not "stop serving".
            var artifact = await edge.Certificates
                .FirstOrDefaultAsync(c => c.ManagedCertificateId == id, ct);
            if (artifact is not null)
            {
                artifact.ManagedCertificateId = null;
                artifact.UpdatedAt = DateTimeOffset.UtcNow;
            }

            edge.ManagedCertificates.Remove(row);
            await edge.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequireAuthorization()
          .WithAudit(AuditActions.ManagedCertificateRemoved, "managed-certificate");

        // Reissue before it is due. Flag plus event, exactly as the tenant-domain button works:
        // the flag makes it certain because the hourly sweep honours it, the event makes it
        // immediate. A dropped message costs an hour, not the reissue.
        app.MapPost("/api/admin/platform/certificates/{id:guid}/reissue", async (
            Guid id, EdgeDbContext edge, IEventPublisher events, CurrentUser me, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }

            if (await edge.ManagedCertificates.FirstOrDefaultAsync(m => m.Id == id, ct) is not { } row)
            {
                return Results.NotFound();
            }

            if (!row.Enabled)
            {
                return Results.BadRequest(new
                {
                    error = "This certificate is disabled, so it is not being renewed. Enable it first.",
                });
            }

            // On the managed row, which always exists. The flag started out on the issued
            // certificate, so a certificate that had never been issued -- the state of every one
            // of them before their first order, and the state somebody is most likely to press
            // this button in -- recorded the request nowhere: no flag, no badge, no history, and
            // a toast saying it had worked.
            row.ReissueRequestedAt = DateTimeOffset.UtcNow;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await edge.SaveChangesAsync(ct);

            // Not cleared here, unlike the tenant-domain reissue: that button clears
            // ConsecutiveFailures because the operator asking IS new information about a DNS
            // record they have just fixed. A managed certificate's ceiling is a rate limit, and
            // an operator pressing a button is not new information about what the CA has already
            // counted. Letting the button clear it would make the guard advisory.
            await events.PublishAsync(
                Subjects.ManagedCertificateReissueRequested,
                new ManagedCertificateReissueRequested(
                    Guid.NewGuid(), DateTimeOffset.UtcNow, row.Id, row.Name),
                ct);

            return Results.Accepted();
        }).RequireAuthorization()
          .WithAudit(AuditActions.ManagedCertificateReissued, "managed-certificate");

        return app;
    }

    private static string[] Clean(string[] identifiers) =>
        [.. identifiers
            .Select(i => i.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(i => i.Length > 0)
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Syntax only, deliberately.
    ///
    /// <para>Whether we hold DNS control over a name can only be answered by the Cloudflare
    /// token, and that token lives in the edge's Vault path alone — the edge's policy grants one
    /// Transit key and its own secrets precisely so a compromise there stops at TLS, and copying
    /// a DNS-edit credential into admin-api to improve a validation message would undo that.
    /// The real answer arrives within a sweep instead: the Cloudflare error is lifted verbatim
    /// into the attempt row, so "no zone in this account contains that name" is what the console
    /// shows.</para>
    /// </summary>
    private static string? Validate(Request body)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return "Give the certificate a name.";
        }

        var identifiers = Clean(body.Identifiers);
        if (identifiers.Length == 0)
        {
            return "List at least one domain for this certificate to cover.";
        }

        if (identifiers.Length > MaxIdentifiers)
        {
            return $"A certificate may cover at most {MaxIdentifiers} names; this one lists {identifiers.Length}.";
        }

        foreach (var identifier in identifiers)
        {
            var bare = identifier.StartsWith("*.", StringComparison.Ordinal) ? identifier[2..] : identifier;

            if (identifier.Length > 255 || bare.Length == 0)
            {
                return $"'{identifier}' is not a usable domain name.";
            }

            // A wildcard is legal only as the whole leftmost label. "*.a.b" is a wildcard;
            // "a.*.b" and "*a.b" are not, and the CA rejects an order containing one.
            if (bare.Contains('*', StringComparison.Ordinal))
            {
                return $"'{identifier}': a wildcard may only be the first label, as in *.example.com.";
            }

            var labels = bare.Split('.');
            if (labels.Length < 2 || labels.Any(l => l.Length is 0 or > 63))
            {
                return $"'{identifier}' is not a usable domain name.";
            }

            if (labels.Any(l => !l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                                || l.StartsWith('-') || l.EndsWith('-')))
            {
                return $"'{identifier}' contains characters that are not valid in a domain name.";
            }
        }

        return null;
    }
}
