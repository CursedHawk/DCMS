using Dcms.Shared.Data;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Data.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// The discriminators the platform console renders.
///
/// <para>Deliberately not added to <see cref="NotificationKinds"/>: that type is reflected over
/// by a test that requires every kind in it to have an i18n entry in both admin locales, and
/// these are rendered by the platform console, which ships one language and hardcodes its
/// strings. Kept as constants anyway, because the wording lives in
/// <c>apps/platform/src/features/notifications/render.ts</c> and a typo on either side degrades
/// silently to showing the operator a raw discriminator.</para>
/// </summary>
public static class PlatformNotificationKinds
{
    public const string CertificateIssued = "certificate.issued";

    /// <summary>The CA refused. Costs an attempt; the fix is usually a DNS record.</summary>
    public const string CertificateFailed = "certificate.failed";

    /// <summary>No order was placed. Costs nothing; the fix is in our own configuration.</summary>
    public const string CertificateBlocked = "certificate.blocked";

    public const string CertificateExpiring = "certificate.expiring";
    public const string CertificateExpired = "certificate.expired";
}

/// <summary>
/// Turns what happened to the platform's own certificates into something an operator is told,
/// rather than something they have to go and look at.
///
/// <para><b>Why it reads the ledger instead of consuming an event.</b> Every outcome already
/// lands durably in <c>edge.managed_certificate_attempts</c> — that table is the rate-limit
/// guard, so it cannot be skipped, and it records the three outcomes that differ in what an
/// operator should do about them. An event stream carrying the same facts would be a second
/// path that can disagree with the first, and it would need the edge to reach a schema it has
/// no grant on. Reading the record that must exist anyway is both smaller and harder to get
/// wrong.</para>
///
/// <para><b>Why a window rather than a watermark.</b> Each pass re-reads the last
/// <c>LookbackHours</c> and lets the dedupe key decide what is new. A stored watermark would
/// have to be correct across restarts, replicas and clock skew to avoid losing a notification;
/// a unique index is correct by construction. It also bounds the first pass after deployment,
/// which would otherwise announce every attempt ever recorded.</para>
/// </summary>
public sealed class CertificateNotificationWorker(
    IServiceProvider services,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<CertificateNotificationWorker> logger) : BackgroundService
{
    private readonly TimeSpan interval = TimeSpan.FromSeconds(
        configuration.GetValue("Notifications:CertificateSweepSeconds", 120));

    private readonly TimeSpan lookback = TimeSpan.FromHours(
        configuration.GetValue("Notifications:CertificateLookbackHours", 24));

    /// <summary>
    /// Matches the edge's own renewal window, so "expiring" means the platform has already
    /// tried and not succeeded — a warning worth reading rather than a countdown.
    /// </summary>
    private readonly int warnWithinDays =
        configuration.GetValue("Notifications:CertificateExpiryWarningDays", 30);

    private const string CertificatesPath = "/certificates";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nothing here is urgent, and a first pass on the startup path would compete with the
        // migrations that create the table it reads.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Certificate notification sweep failed; retrying next cycle.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        // One replica per pass. Without it every replica would race on the same unique index and
        // do the same work to discover the other one had already done it.
        await using var leadership = await PostgresAdvisoryLock.TryAcquireAsync(
            connectionString, PostgresAdvisoryLock.PlatformNotificationSweepLockKey, logger, ct);
        if (leadership is null)
        {
            return;
        }

        using var scope = services.CreateScope();
        var edge = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IPlatformNotificationPublisher>();

        await RaiseAttemptOutcomesAsync(edge, publisher, ct);
        await RaiseExpiryWarningsAsync(edge, publisher, ct);
    }

    private async Task RaiseAttemptOutcomesAsync(
        EdgeDbContext edge, IPlatformNotificationPublisher publisher, CancellationToken ct)
    {
        var since = clock.GetUtcNow() - lookback;

        var attempts = await edge.ManagedCertificateAttempts.AsNoTracking()
            .Where(a => a.AttemptedAt >= since)
            .OrderBy(a => a.AttemptedAt)
            .Join(edge.ManagedCertificates.AsNoTracking(), a => a.ManagedCertificateId, m => m.Id,
                (a, m) => new { Attempt = a, Managed = m })
            .ToListAsync(ct);

        foreach (var row in attempts)
        {
            // The three outcomes are three different pieces of news, and conflating them is the
            // failure this whole area has already had once: "the CA refused" sends an operator
            // to look at a DNS record, "we never asked" sends them to look at our own Vault.
            var (kind, severity) = row.Attempt switch
            {
                { Succeeded: true } =>
                    (PlatformNotificationKinds.CertificateIssued, NotificationSeverity.Success),
                { ReachedCa: true } =>
                    (PlatformNotificationKinds.CertificateFailed, NotificationSeverity.Error),
                _ => (PlatformNotificationKinds.CertificateBlocked, NotificationSeverity.Warning),
            };

            await publisher.RaiseAsync(
                new PlatformNotificationRequest(
                    kind,
                    severity,
                    // The attempt, not the pass: one row is one thing that happened.
                    $"certificate.attempt:{row.Attempt.Id}",
                    new
                    {
                        name = row.Managed.Name,
                        identifiers = row.Managed.Identifiers,
                        error = row.Attempt.Error,
                        attemptedAt = row.Attempt.AttemptedAt,
                    },
                    CertificatesPath,
                    "managed-certificate",
                    row.Managed.Id),
                ct);
        }
    }

    private async Task RaiseExpiryWarningsAsync(
        EdgeDbContext edge, IPlatformNotificationPublisher publisher, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var threshold = now.AddDays(warnWithinDays);

        var certificates = await edge.Certificates.AsNoTracking()
            .Where(c => c.ManagedCertificateId != null && c.NotAfter <= threshold)
            .Join(edge.ManagedCertificates.AsNoTracking(), c => c.ManagedCertificateId, m => m.Id,
                (c, m) => new { Certificate = c, Managed = m })
            .ToListAsync(ct);

        foreach (var row in certificates)
        {
            var expired = row.Certificate.NotAfter <= now;

            await publisher.RaiseAsync(
                new PlatformNotificationRequest(
                    expired
                        ? PlatformNotificationKinds.CertificateExpired
                        : PlatformNotificationKinds.CertificateExpiring,
                    expired ? NotificationSeverity.Error : NotificationSeverity.Warning,
                    // Keyed on the expiry being warned about, so a renewal that moves NotAfter
                    // is allowed to warn again next time and the same deadline never warns twice.
                    $"certificate.{(expired ? "expired" : "expiring")}:{row.Managed.Id}:{row.Certificate.NotAfter:O}",
                    new
                    {
                        name = row.Managed.Name,
                        identifiers = row.Managed.Identifiers,
                        notAfter = row.Certificate.NotAfter,
                        daysRemaining = (int)Math.Floor((row.Certificate.NotAfter - now).TotalDays),
                    },
                    CertificatesPath,
                    "managed-certificate",
                    row.Managed.Id),
                ct);
        }
    }
}
