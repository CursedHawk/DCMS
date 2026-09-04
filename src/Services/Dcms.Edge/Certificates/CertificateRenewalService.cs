using Dcms.Shared.Data;
using Dcms.Shared.Data.Edge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates;

/// <summary>
/// Renews certificates before they expire.
///
/// <para>This is the background job whose absence is invisible until it is catastrophic: nothing
/// about a working platform tells you renewal has stopped, until every tenant's site goes to a
/// browser warning on the same afternoon. It logs a summary on every pass — including a pass
/// that renewed nothing — so "the sweep is running" is an observable fact rather than an
/// assumption.</para>
///
/// <para>Under an advisory lock, so exactly one replica renews per pass. Two replicas renewing
/// the same certificate would double the calls counted against the CA's rate limit for no
/// benefit, and the loser would overwrite the winner's row with an equally valid but different
/// certificate.</para>
/// </summary>
public sealed class CertificateRenewalService(
    IServiceProvider services,
    ICertificateStore store,
    IAcmeIssuer issuer,
    IOptions<CertificateOptions> options,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<CertificateRenewalService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;
        if (!config.TlsEnabled)
        {
            logger.LogInformation("TLS is off; the certificate renewal sweep will not run.");
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
            .Where(c => c.Source == CertificateSource.DcmsManaged && c.NotAfter <= threshold)
            .Select(c => c.Hostname)
            .ToListAsync(ct);

        var renewed = 0;
        var failed = 0;
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
                var issued = await issuer.IssueAsync(hostname, ct);
                await store.SaveAsync(hostname, issued.PemChain, issued.PemPrivateKey, CertificateSource.DcmsManaged, ct);
                renewed++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Renewal failed for {Hostname}.", hostname);
                await store.RecordFailureAsync(hostname, ex.Message, CancellationToken.None);
                failed++;
            }
        }

        // Logged even when everything is zero. A sweep that finds nothing to do and a sweep that
        // is not running produce identical silence otherwise, and only one of them is fine.
        logger.LogInformation(
            "Certificate renewal sweep: {Due} due, {Renewed} renewed, {Failed} failed.",
            due.Count, renewed, failed);
    }
}
