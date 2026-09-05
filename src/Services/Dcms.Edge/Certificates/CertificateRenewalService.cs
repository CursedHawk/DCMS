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

        // Everything we are allowed to serve and cannot.
        //
        // This is what makes "the store is empty" a self-correcting state rather than a
        // permanent one. Without it a hostname with no certificate is only ever attempted
        // inside a visitor's TLS handshake, bounded by OnDemandTimeoutSeconds -- and a full
        // ACME order usually takes longer than that, so the first visit fails, records a
        // failure, backs off, and the next visit fails further away. Nothing recovers it; a
        // browser just gets ERR_CONNECTION_CLOSED forever.
        //
        // HELD MEANS A USABLE KEY, NOT A ROW. RecordFailureAsync writes a placeholder row --
        // hostname, error, failure count, no key and NotAfter at -infinity -- so a hostname
        // that has only ever failed HAS a row and serves nothing. Counting those as held is
        // how a completely dark platform reported "3 missing" while nine hostnames were
        // refusing every handshake: the number an operator reads first, understating the
        // outage by a factor of three.
        var heldSet = (await db.Certificates.AsNoTracking()
                .Where(c => c.EncryptedPrivateKey != "")
                .Select(c => c.Hostname)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var allowed = (await allowList.AllowedHostnamesAsync(ct))
            .Select(CertificateStoreNormalize)
            .ToList();
        var unusable = allowed.Where(h => !heldSet.Contains(h)).ToList();

        // A placeholder row is already in `due` (its NotAfter is -infinity), so ordering it
        // here as well would spend two of the CA's ~50 weekly certificates on one hostname.
        // The count above is what gets reported; this is what gets ordered.
        var dueSet = due.ToHashSet(StringComparer.Ordinal);
        var missing = unusable.Where(h => !dueSet.Contains(h)).ToList();

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
                + "stored. {Missing} hostnames cannot serve TLS and {Due} are due. Check "
                + "VAULT_ROLE_ID_EDGE / VAULT_SECRET_ID_EDGE and that infra/vault/apply.sh has "
                + "been run. Not ordering, because an order that cannot be stored still spends "
                + "the CA rate limit.",
                unusable.Count, due.Count);
            metrics.SetEdgeCertificateState("missing", unusable.Count);
            metrics.SetEdgeCertificateState("due", due.Count);
            return;
        }

        // Serially, and inside the advisory lock. The CA rate-limits by account, and a burst of
        // parallel orders -- which is exactly what a cold start looks like -- is the shape that
        // trips it.
        var deferred = new List<string>();

        foreach (var hostname in missing)
        {
            switch (await OrderAsync(hostname, isRenewal: false, config, deferred, ct))
            {
                case true: issued++; break;
                case false: failed++; break;
            }
        }

        foreach (var hostname in due)
        {
            switch (await OrderAsync(hostname, isRenewal: true, config, deferred, ct))
            {
                case true: renewed++; break;
                case false: failed++; break;
            }
        }

        // Named, not just counted. A hostname inside its backoff is skipped silently otherwise,
        // and that is the shape of "I fixed the cause and nothing happened": after six failures
        // the wait is hours, so an operator who repairs Vault at noon sees no certificate and no
        // explanation until mid-afternoon. Saying which hostnames are waiting, and until when,
        // is the difference between a wait and an unexplained silence.
        if (deferred.Count > 0)
        {
            logger.LogWarning(
                "Deferred by failure backoff this pass: {Deferred}. Their retry times are in "
                + "edge.certificates.LastAttemptAt + the doubling backoff; the recorded cause is "
                + "in LastError.",
                string.Join(", ", deferred));
        }

        // Published on every pass, including an empty one. These are the numbers an alert is
        // built on, and renewal stopping is invisible until it is catastrophic: nothing about a
        // working platform says it has stopped, until every tenant's site goes to a browser
        // warning on the same afternoon.
        //
        // RECOUNTED AFTER the work, not reused from before it. The first version reported the
        // start-of-pass figure alongside end-of-pass issue and renew counts, so a sweep that had
        // just fixed everything still logged "4 cannot serve TLS, 6 renewed" -- which reads as
        // four hostnames still broken, and sent me looking for them. The gauge had the same
        // flaw and would have alerted for a full hour after the platform recovered.
        // A hostname that is serving TLS and is not due is not being ordered for, so whatever
        // failure is recorded against it is protecting a rate limit from a request nobody is
        // going to make. Leaving it there keeps the `failing` gauge -- the one an alert fires on
        // -- reading the last outage forever: after Vault handed back expired tokens on
        // 2026-09-05 every provisioned tenant site kept one to five recorded failures behind a
        // certificate that was perfectly good. Cleared here, so recovery is something the sweep
        // reports rather than something an operator has to go and confirm by hand.
        var healed = await store.ClearBackoffForHealthyAsync(threshold, ct);
        if (healed > 0)
        {
            logger.LogInformation(
                "Cleared a stale failure record on {Count} hostname(s) that hold a working "
                + "certificate and are not due for renewal.", healed);
        }

        var usable = await db.Certificates
            .Where(c => c.EncryptedPrivateKey != "")
            .Select(c => c.Hostname)
            .ToListAsync(ct);
        var usableSet = usable.ToHashSet(StringComparer.Ordinal);
        var stillUnusable = allowed.Count(h => !usableSet.Contains(h));

        var failing = await db.Certificates.CountAsync(c => c.ConsecutiveFailures > 0, ct);
        metrics.SetEdgeCertificateState("total", usable.Count);
        metrics.SetEdgeCertificateState("due", due.Count);
        metrics.SetEdgeCertificateState("missing", stillUnusable);
        metrics.SetEdgeCertificateState("failing", failing);

        // Logged even when everything is zero, for the same reason. A sweep that finds nothing
        // to do and a sweep that is not running produce identical silence otherwise.
        logger.LogInformation(
            "Certificate sweep: {Missing} cannot serve TLS, {Issued} issued, {Due} due, "
            + "{Renewed} renewed, {Failed} failed, {Total} usable.",
            stillUnusable, issued, due.Count, renewed, failed, usable.Count);
    }

    /// <summary>
    /// Orders one certificate. True issued, false failed, null deferred by the backoff.
    ///
    /// <para><b>The CA's failures and ours are recorded differently, and that distinction is
    /// load-bearing.</b> <c>RecordFailureAsync</c> drives an exponentially doubling backoff that
    /// exists to protect the account's rate limit from a hostname whose authorization keeps
    /// failing — a tenant whose DNS record was deleted after verification. Applying it to a
    /// failure that never reached the CA punishes the platform for its own outage: when Vault
    /// Transit was unreachable, six such "failures" were recorded against every platform
    /// hostname, so repairing Vault would have been followed by hours of the sweep skipping
    /// them for a backoff no CA ever asked for.</para>
    ///
    /// <para>So only <c>IssueAsync</c> throwing extends it. A store failure after a successful
    /// order is logged loudly and left retryable: the CA has already spent the certificate, and
    /// the next pass should take it the moment whatever broke is fixed.</para>
    /// </summary>
    private async Task<bool?> OrderAsync(
        string hostname, bool isRenewal, CertificateOptions config, List<string> deferred, CancellationToken ct)
    {
        if (await store.RetryNotBeforeAsync(hostname, config, ct) is not null)
        {
            deferred.Add(hostname);
            return null;
        }

        IssuedCertificate certificate;
        try
        {
            certificate = await issuer.IssueAsync(hostname, ct);
        }
        catch (CertificateIssuanceUnavailableException ex)
        {
            // We never got as far as the CA -- so nothing was spent, and nothing is backed off.
            logger.LogError(
                ex,
                "{What} for {Hostname} could not be attempted; the CA was never asked, so this is "
                + "not backed off and the next pass will try again.",
                isRenewal ? "Renewal" : "Issuance", hostname);
            metrics.EdgeCertificate("failed");
            return false;
        }
        catch (Exception ex)
        {
            // The CA said no. This is what the backoff is for.
            logger.LogError(ex, "{What} failed for {Hostname}.", isRenewal ? "Renewal" : "Issuance", hostname);
            await store.RecordFailureAsync(hostname, ex.Message, CancellationToken.None);
            metrics.EdgeCertificate("failed");
            return false;
        }

        try
        {
            await store.SaveAsync(
                hostname, certificate.PemChain, certificate.PemPrivateKey, CertificateSource.DcmsManaged, ct);
        }
        catch (Exception ex)
        {
            // Ours, not the CA's -- so no backoff. Deliberately loud: a certificate was issued
            // and then thrown away, which spends the account's weekly budget for nothing.
            logger.LogError(
                ex,
                "A certificate was ISSUED for {Hostname} and could not be stored, so it is lost. "
                + "The CA counted it against the rate limit. Not backing off: the next pass "
                + "should retry as soon as the store is working.",
                hostname);
            metrics.EdgeCertificate("failed");
            return false;
        }

        metrics.EdgeCertificate(isRenewal ? "renewed" : "issued");
        return true;
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
