using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Data;
using Dcms.Shared.Data.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// Drops notifications nobody needs any more. Without this the tables grow forever: every site
/// publish, every upload and every content change appends rows that stop being interesting
/// within a day.
///
/// <para>Deletion happens in two steps — read or dismissed recipient rows past the retention
/// window first, then notifications left with no recipients at all. The other order would rely
/// on the cascade and would take notifications still unread by somebody else, since one
/// notification row is shared by every one of its recipients.</para>
///
/// <para><b>Audit.</b> Both statements are set-based, so nothing can produce a field diff — the
/// rows are gone before anything could read them. Per-statement capture is therefore suppressed
/// in favour of one record for the whole pass carrying the cutoff and the counts, which is the
/// same trade <c>AnalyticsRetentionWorker</c> makes and for the same reason.</para>
/// </summary>
public sealed class NotificationRetentionWorker(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<NotificationRetentionWorker> logger) : BackgroundService
{
    private readonly int _retentionDays = configuration.GetValue("Notifications:RetentionDays", 90);

    private readonly TimeSpan _interval =
        TimeSpan.FromHours(configuration.GetValue("Notifications:RetentionSweepHours", 24));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nothing here is urgent, and a sweep on the startup path would compete with migrations.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notification retention sweep failed; retrying next cycle.");
            }
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        // One replica per pass. The work is idempotent, so concurrent sweeps would be correct
        // but would each pay for the same scan and produce a duplicate audit record.
        await using var leadership = await PostgresAdvisoryLock.TryAcquireAsync(
            connectionString, PostgresAdvisoryLock.NotificationRetentionLockKey, logger, ct);

        if (leadership is null)
        {
            logger.LogDebug("Another replica is running notification retention; skipping this pass.");
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<IAuditRecorder>();
        var auditScope = scope.ServiceProvider.GetRequiredService<AuditScope>();
        var ambient = scope.ServiceProvider.GetRequiredService<AuditAmbient>();
        using var ambientScope = ambient.Enter(auditScope);

        // One record for the pass instead of one per statement. See the class remarks.
        using var bulkSuppressed = auditScope.SuppressBulkCapture();

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_retentionDays);

        // IgnoreQueryFilters because retention is cross-tenant by nature and this worker has
        // no ambient tenant -- the filter would match nothing at all.
        var recipients = await db.Recipients.IgnoreQueryFilters()
            .Where(r => (r.ReadAt != null && r.ReadAt < cutoff)
                     || (r.DismissedAt != null && r.DismissedAt < cutoff))
            .ExecuteDeleteAsync(ct);

        var orphans = await db.Notifications.IgnoreQueryFilters()
            .Where(n => !db.Recipients.IgnoreQueryFilters().Any(r => r.NotificationId == n.Id))
            .ExecuteDeleteAsync(ct);

        if (recipients == 0 && orphans == 0)
        {
            return;
        }

        recorder.Record(AuditActions.NotificationsPruned)
            .Platform()
            .As(AuditCategory.TenantState)
            .For("notifications", "recipients", $"notifications read or dismissed over {_retentionDays} days ago")
            .With("cutoff", cutoff.ToString("O"))
            .With("retentionDays", _retentionDays)
            .With("recipientRowsDeleted", recipients)
            .With("notificationsDeleted", orphans);

        await recorder.FlushAsync(ct);

        logger.LogInformation(
            "Notification retention removed {Recipients} recipient row(s) and {Orphans} notification(s).",
            recipients, orphans);
    }
}
