using System.Text;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Telemetry;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates;

/// <summary>
/// Keeps the platform in certificates: issues the ones that are missing, renews the ones that
/// are due, and honours an operator's request to replace one early.
///
/// <para>This is the mechanism now, not a safety net behind on-demand issuance. Issuing inside a
/// visitor's TLS handshake is bounded by <c>OnDemandTimeoutSeconds</c> and a full ACME order
/// usually takes longer, so a hostname that is only ever attempted on demand fails the first
/// visit, records a failure, backs off, and fails the next one further away. On-demand stays as
/// the path for a domain verified between two sweeps; it is not what the platform depends on.
/// </para>
///
/// <para>Its absence is invisible until it is catastrophic: nothing about a working platform
/// tells you issuance has stopped, until every site goes to a browser warning on the same
/// afternoon. It logs a summary on every pass — including a pass that did nothing — so "the
/// sweep is running" is an observable fact rather than an assumption.</para>
///
/// <para>Under an advisory lock, so exactly one replica renews per pass. Two replicas renewing
/// the same certificate would double the calls counted against the CA's rate limit for no
/// benefit, and the loser would overwrite the winner's row with an equally valid but different
/// certificate.</para>
/// </summary>
public sealed class CertificateRenewalService(
    IServiceProvider services,
    ICertificateStore store,
    ITlsAllowList allowList,
    IAcmeIssuer issuer,
    IOptions<CertificateOptions> options,
    IConfiguration configuration,
    DcmsMetrics metrics,
    TimeProvider clock,
    ILogger<CertificateRenewalService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        if (!config.TlsEnabled)
        {
            logger.LogInformation("TLS is off; the certificate sweep will not run.");
            return;
        }

        var period = TimeSpan.FromMinutes(Math.Max(1, config.RenewalSweepMinutes));
        using var timer = new PeriodicTimer(period);

        // A first pass at startup rather than after one full period: a deployment that has been
        // down over a renewal window should catch up immediately, not in an hour.
        do
        {
            try
            {
                await SweepAsync(config, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Certificate renewal sweep failed; retrying next pass.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepAsync(CertificateOptions config, CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? throw new InvalidOperationException(
                                   "ConnectionStrings:Postgres is required for certificate renewal.");

        await using var electionLock = await PostgresAdvisoryLock.AcquireAsync(
            connectionString, PostgresAdvisoryLock.EdgeCertificateRenewalLockKey, logger, ct);

        var threshold = clock.GetUtcNow().AddDays(config.RenewBeforeDays);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        var due = await db.Certificates.AsNoTracking()
            // Custom certificates are excluded on purpose: the platform holds no authority to
            // reissue one, so a sweep that "renewed" it would silently replace a tenant's own
            // certificate with a Let's Encrypt one. Expiry is reported instead, in the admin UI.
            // ReissueRequestedAt is the backstop for the operator's "reissue now" button: the
            // event admin-api publishes makes it immediate, and this makes it certain.
            .Where(c => c.Source == CertificateSource.DcmsManaged
                        && (c.NotAfter <= threshold || c.ReissueRequestedAt != null))
            .Select(c => c.Hostname)
            .ToListAsync(ct);

        // Everything we are allowed to hold a certificate for and do not.
        //
        // This is what makes "the store is empty" a self-correcting state rather than a
        // permanent one. Without it a hostname with no row is only ever attempted inside a
        // visitor's TLS handshake, bounded by OnDemandTimeoutSeconds -- and a full ACME order
        // usually takes longer than that, so the first visit fails, records a failure, backs
        // off, and the next visit fails further away. Nothing recovers it; a browser just gets
        // ERR_CONNECTION_CLOSED forever.
        var held = await db.Certificates.AsNoTracking()
            .Select(c => c.Hostname)
            .ToListAsync(ct);
        var heldSet = held.ToHashSet(StringComparer.Ordinal);
        var missing = (await allowList.AllowedHostnamesAsync(ct))
            .Select(CertificateStoreNormalize)
            .Where(h => !heldSet.Contains(h))
            .ToList();

        var renewed = 0;
        var issued = 0;
        var failed = 0;

        // Nothing below can be STORED without Transit -- SaveAsync encrypts the private key
        // through it -- and an order that cannot be stored still counts against the CA's
        // budget. With ~50 certificates per registered domain per week and every managed
        // subdomain sharing one, a sweep that kept ordering into a broken Transit would spend
        // the whole week's allowance in an afternoon and turn a ten-minute Vault fix into a
        // seven-day block. So it is checked once per pass, before a single order.
        if (!await TransitWorksAsync(ct))
        {
            logger.LogError(
                "Certificate sweep skipped: Vault Transit is unusable, so no certificate can be "
                + "stored. {Missing} hostnames have none and {Due} are due. Check "
                + "VAULT_ROLE_ID_EDGE / VAULT_SECRET_ID_EDGE and that infra/vault/apply.sh has "
                + "been run. Not ordering, because an order that cannot be stored still spends "
                + "the CA rate limit.",
                missing.Count, due.Count);
            metrics.SetEdgeCertificateState("missing", missing.Count);
            metrics.SetEdgeCertificateState("due", due.Count);
            return;
        }

        foreach (var hostname in missing)
        {
            // Serially and inside the same advisory lock as the renewals below, for the same
            // reason: the CA rate-limits by account, and a burst of parallel orders -- which is
            // exactly what a cold start looks like -- is the shape that trips it.
            if (await store.RetryNotBeforeAsync(hostname, config, ct) is not null)
            {
                continue;
            }

            try
            {
                var certificate = await issuer.IssueAsync(hostname, ct);
                await store.SaveAsync(
                    hostname, certificate.PemChain, certificate.PemPrivateKey, CertificateSource.DcmsManaged, ct);
                metrics.EdgeCertificate("issued");
                issued++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "First issuance failed for {Hostname}.", hostname);
                await store.RecordFailureAsync(hostname, ex.Message, CancellationToken.None);
                metrics.EdgeCertificate("failed");
                failed++;
            }
        }

        foreach (var hostname in due)
        {
            // Serially, not in parallel. The CA rate-limits by account, and a burst of parallel
            // orders after an outage is exactly the shape that trips it.
            if (await store.RetryNotBeforeAsync(hostname, config, ct) is not null)
            {
                continue;
            }

            try
            {
                var certificate = await issuer.IssueAsync(hostname, ct);
                await store.SaveAsync(
                    hostname, certificate.PemChain, certificate.PemPrivateKey, CertificateSource.DcmsManaged, ct);
                metrics.EdgeCertificate("renewed");
                renewed++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Renewal failed for {Hostname}.", hostname);
                await store.RecordFailureAsync(hostname, ex.Message, CancellationToken.None);
                metrics.EdgeCertificate("failed");
                failed++;
            }
        }

        // Published on every pass, including an empty one. These are the numbers an alert is
        // built on, and renewal stopping is invisible until it is catastrophic: nothing about a
        // working platform says it has stopped, until every tenant's site goes to a browser
        // warning on the same afternoon.
        var total = await db.Certificates.CountAsync(ct);
        var failing = await db.Certificates.CountAsync(c => c.ConsecutiveFailures > 0, ct);
        metrics.SetEdgeCertificateState("total", total);
        metrics.SetEdgeCertificateState("due", due.Count);
        metrics.SetEdgeCertificateState("missing", missing.Count);
        metrics.SetEdgeCertificateState("failing", failing);

        // Logged even when everything is zero, for the same reason. A sweep that finds nothing
        // to do and a sweep that is not running produce identical silence otherwise.
        logger.LogInformation(
            "Certificate sweep: {Missing} missing, {Issued} issued, {Due} due, {Renewed} renewed, "
            + "{Failed} failed, {Total} held.",
            missing.Count, issued, due.Count, renewed, failed, total);
    }

    /// <summary>Same normalisation the store keys on, so "held" and "allowed" compare.</summary>
    private static string CertificateStoreNormalize(string hostname) => CertificateStore.Normalize(hostname);

    /// <summary>
    /// A round trip through the Transit key every private key is encrypted with. Cheap, and it
    /// is the one dependency whose absence has no symptom of its own: certificates simply never
    /// appear, and every handshake is refused with nothing in the browser to say why.
    /// </summary>
    private async Task<bool> TransitWorksAsync(CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var transit = scope.ServiceProvider.GetRequiredService<ITransitEncryptor>();
            var ciphertext = await transit.EncryptAsync(
                VaultTransitServiceCollectionExtensions.TlsKeysKey,
                Encoding.UTF8.GetBytes("edge-sweep"), ct);
            var roundTripped = Encoding.UTF8.GetString(
                await transit.DecryptAsync(VaultTransitServiceCollectionExtensions.TlsKeysKey, ciphertext, ct));

            return roundTripped == "edge-sweep";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Vault Transit round-trip failed.");
            return false;
        }
    }
}
