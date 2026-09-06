using Dcms.Shared.Data.Edge;
using Dcms.Shared.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates;

/// <summary>How one sweep of the managed certificates went.</summary>
/// <param name="Issued">Certificates obtained for the first time.</param>
/// <param name="Renewed">Certificates replaced before expiry.</param>
/// <param name="Failed">Orders the CA refused, or that could not be stored.</param>
/// <param name="Deferred">Orders held back by a rate-limit ceiling.</param>
public readonly record struct ManagedSweepResult(int Issued, int Renewed, int Failed, int Deferred);

/// <summary>
/// Issues and renews the certificates DCMS holds for its own domains — the rows in
/// <c>edge.managed_certificates</c>.
///
/// <para>Separate from <see cref="CertificateProvisioner"/>, which serves one hostname at a time
/// for a tenant, because almost nothing about the two is the same: this one orders a fixed set of
/// identifiers rather than resolving one from a request, validates over DNS-01 rather than
/// HTTP-01, is never reachable from a TLS handshake, and answers to a rate-limit ceiling whose
/// blast radius is the whole platform rather than one site. Folding them together would mean the
/// tenant path could reach the wildcard's budget.</para>
///
/// <para><b>Never called from a handshake.</b> A managed order takes a DNS propagation wait plus
/// a CA validation — minutes, not the seconds a handshake can hold — and issuing one under a
/// visitor would guarantee the timeout, record a failure, and spend a rate limit for a connection
/// that was aborted anyway.</para>
/// </summary>
public sealed class ManagedCertificateProvisioner(
    IServiceProvider services,
    ICertificateStore store,
    IAcmeIssuer issuer,
    IOptions<CertificateOptions> options,
    DcmsMetrics metrics,
    TimeProvider clock,
    ILogger<ManagedCertificateProvisioner> logger)
{
    /// <summary>
    /// The seeded platform certificate's id. Fixed rather than generated, which is what makes
    /// seeding idempotent across restarts and across replicas.
    /// </summary>
    public static readonly Guid PlatformCertificateId = new("9d3f6f2e-0b6a-4d64-9d0a-1c2f5f7a8e11");

    /// <summary>
    /// Creates the platform's own managed certificate from configuration, if it is not there.
    ///
    /// <para><b>Create-if-absent, never update.</b> The row is editable from the platform console,
    /// and re-asserting configuration over it on every boot would silently undo an operator's
    /// change the next time the container restarted — a change they had every reason to believe
    /// had taken. Configuration supplies the starting point; after that the table is the truth.
    /// </para>
    /// </summary>
    public async Task SeedAsync(CancellationToken ct)
    {
        var config = options.Value;
        var identifiers = config.ManagedIdentifierList;
        if (identifiers.Length == 0)
        {
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();

        if (await db.ManagedCertificates.AnyAsync(m => m.Id == PlatformCertificateId, ct))
        {
            return;
        }

        db.ManagedCertificates.Add(new EdgeManagedCertificate
        {
            Id = PlatformCertificateId,
            Name = config.ManagedCertificateName,
            Identifiers = [.. identifiers.Select(CertificateStore.Normalize)],
            Enabled = true,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Seeded the platform's managed certificate '{Name}' covering {Identifiers}.",
                config.ManagedCertificateName, string.Join(", ", identifiers));
        }
        catch (DbUpdateException)
        {
            // A sibling replica inserted it between the check and the write. The fixed primary
            // key is what turns that race into a no-op rather than a duplicate.
        }
    }

    /// <summary>
    /// Orders whatever is missing, due, or has been asked for. Called by the renewal sweep, under
    /// the same advisory lock, so exactly one replica does this per pass.
    /// </summary>
    public async Task<ManagedSweepResult> SweepAsync(CancellationToken ct)
    {
        var config = options.Value;
        var now = clock.GetUtcNow();
        var threshold = now.AddDays(config.RenewBeforeDays);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();

        var managed = await db.ManagedCertificates.Where(m => m.Enabled).ToListAsync(ct);
        var result = new ManagedSweepResult();

        foreach (var certificate in managed)
        {
            if (certificate.Identifiers.Length == 0)
            {
                logger.LogWarning(
                    "Managed certificate '{Name}' has no identifiers; skipping.", certificate.Name);
                continue;
            }

            var artifact = await db.Certificates.AsNoTracking()
                .FirstOrDefaultAsync(c => c.ManagedCertificateId == certificate.Id, ct);

            var isRenewal = artifact is not null && artifact.IsUsable(now);
            var due = artifact is null
                      || !artifact.IsUsable(now)
                      || artifact.NotAfter <= threshold
                      || artifact.ReissueRequestedAt is not null;

            if (!due)
            {
                continue;
            }

            var budget = await ManagedCertificateGuard.EvaluateAsync(db, certificate, config, now, ct);
            if (!budget.Allowed)
            {
                logger.LogWarning(
                    "Not ordering managed certificate '{Name}' ({Identifiers}): {Reason}",
                    certificate.Name, string.Join(", ", certificate.Identifiers), budget.Reason);
                result = result with { Deferred = result.Deferred + 1 };
                continue;
            }

            switch (await OrderAsync(db, certificate, isRenewal, ct))
            {
                case true when isRenewal: result = result with { Renewed = result.Renewed + 1 }; break;
                case true: result = result with { Issued = result.Issued + 1 }; break;
                case false: result = result with { Failed = result.Failed + 1 }; break;
            }
        }

        return result;
    }

    /// <summary>
    /// Orders one managed certificate. True stored, false failed.
    ///
    /// <para>The three outcomes are kept apart deliberately, exactly as in
    /// <see cref="CertificateProvisioner"/>: an order the CA never heard about costs nothing and
    /// is recorded nowhere, an order the CA refused is charged to the hourly failure ceiling, and
    /// a certificate that was issued and could not be stored is charged to the weekly issuance
    /// ceiling — because the CA has already counted it, whatever happened here afterwards.</para>
    /// </summary>
    private async Task<bool> OrderAsync(
        EdgeDbContext db, EdgeManagedCertificate certificate, bool isRenewal, CancellationToken ct)
    {
        var what = isRenewal ? "Renewal" : "Issuance";
        var label = CertificateStore.Normalize(certificate.Identifiers[0]);

        IssuedCertificate issued;
        try
        {
            issued = await issuer.IssueAsync(certificate.Identifiers, ct);
        }
        catch (CertificateIssuanceUnavailableException ex)
        {
            // The CA was never asked, so nothing was spent and nothing is recorded. The next
            // sweep tries again the moment whatever is broken is fixed.
            logger.LogError(
                ex,
                "{What} of managed certificate '{Name}' could not be attempted; the CA was never "
                + "asked, so no rate limit was spent and this is not held back.",
                what, certificate.Name);
            metrics.EdgeCertificate("failed");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{What} of managed certificate '{Name}' failed.", what, certificate.Name);
            await ManagedCertificateGuard.RecordAttemptAsync(
                db, certificate, succeeded: false, ex.Message, clock.GetUtcNow(), CancellationToken.None);
            await store.RecordFailureAsync(label, ex.Message, CancellationToken.None);
            metrics.EdgeCertificate("failed");
            return false;
        }

        // Recorded BEFORE the store call. The CA has issued and counted it; whether we manage to
        // write it down does not give the certificate back. Recording it only on a successful
        // save would let a broken Vault turn into an unbounded issuance loop, spending the real
        // five-per-week limit while this guard believed it had spent nothing.
        await ManagedCertificateGuard.RecordAttemptAsync(
            db, certificate, succeeded: true, null, clock.GetUtcNow(), CancellationToken.None);

        try
        {
            await store.SaveAsync(
                label, issued.PemChain, issued.PemPrivateKey, CertificateSource.DcmsManaged,
                certificate.Id, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "A managed certificate was ISSUED for '{Name}' and could not be stored, so it is "
                + "lost. The CA counted it against the rate limit; {Remaining} issuance(s) remain "
                + "for these identifiers this week.",
                certificate.Name,
                Math.Max(0, options.Value.ManagedIssuancesPerWeek - 1));
            metrics.EdgeCertificate("failed");
            return false;
        }

        metrics.EdgeCertificate(isRenewal ? "renewed" : "issued");
        logger.LogInformation(
            "{What} of managed certificate '{Name}' succeeded: {Identifiers}.",
            what, certificate.Name, string.Join(", ", certificate.Identifiers));
        return true;
    }

    /// <summary>
    /// Every hostname currently covered by a usable managed certificate, as a matcher.
    ///
    /// <para>Used by the per-hostname sweep so it does not order a certificate for a name the
    /// wildcard already serves. Without it the platform would hold twelve per-host certificates
    /// <i>and</i> the wildcard, renewing all thirteen and spending the rate limit this whole
    /// design exists to protect.</para>
    /// </summary>
    public async Task<Func<string, bool>> CoverageAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        var now = clock.GetUtcNow();

        var covered = await db.Certificates.AsNoTracking()
            .Where(c => c.ManagedCertificateId != null
                        && c.EncryptedPrivateKey != ""
                        && c.NotAfter > now
                        && c.NotBefore <= now)
            .Select(c => c.SubjectAlternativeNames)
            .ToListAsync(ct);

        var names = covered.SelectMany(s => s).ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0)
        {
            return _ => false;
        }

        return hostname =>
        {
            var normalized = CertificateStore.Normalize(hostname);
            return names.Contains(normalized)
                   || (CertificateStore.WildcardParent(normalized) is { } parent && names.Contains(parent));
        };
    }
}
