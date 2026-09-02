using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// Notifies when an invitation lapses unaccepted.
///
/// <para><b>Why this worker has to exist at all.</b> Every other notification is a reaction
/// to something the platform already announces. Invitation expiry announces nothing: it is a
/// passive <c>ExpiresAt</c> column, evaluated at read time by <c>Invitation.IsPending</c> and
/// by the list endpoint's <c>expired = r.expiresAt &lt;= now</c>. Nothing has ever run on a
/// timer over <c>tenancy.invitations</c>, so an invitation that quietly lapses is invisible
/// unless someone happens to open the members page. That silence is the gap this closes.</para>
///
/// <para>Claiming uses <c>FOR UPDATE SKIP LOCKED</c> so several admin-api replicas can run
/// this concurrently and each invitation is reported once — the same shape as
/// <c>ScheduledPublishWorker</c>. The <c>ExpiredNotifiedAt</c> stamp is what makes it once
/// *ever* rather than once per poll.</para>
/// </summary>
public sealed class InvitationExpiryWorker(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<InvitationExpiryWorker> logger) : BackgroundService
{
    // Expiry is not latency-sensitive: the invitation has already been dead for some part of
    // its seven-day life. Five minutes keeps the query rare and the news timely enough.
    private readonly TimeSpan _pollInterval =
        TimeSpan.FromSeconds(configuration.GetValue("Notifications:InvitationExpiryPollSeconds", 300));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Invitation-expiry sweep failed; retrying next poll.");
            }
            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<INotificationPublisher>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Raw SQL for the row lock. Columns are EF's default PascalCase identifiers (only
        // table names are snake_cased), so they must be double-quoted.
        var due = await db.Invitations.FromSqlRaw(
            """
            SELECT * FROM tenancy.invitations
            WHERE "AcceptedAt" IS NULL
              AND "ExpiredNotifiedAt" IS NULL
              AND "ExpiresAt" <= now()
            ORDER BY "ExpiresAt"
            FOR UPDATE SKIP LOCKED
            LIMIT 100
            """).IgnoreQueryFilters().ToListAsync(ct);

        if (due.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var invitation in due)
        {
            invitation.ExpiredNotifiedAt = now;
        }

        // Stamp and commit before notifying. If the process dies between the two, the tenant
        // loses one notification; the other order risks notifying the same lapse on every
        // poll forever, which is far more annoying and far harder to stop.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        foreach (var invitation in due)
        {
            await publisher.RaiseAsync(new NotificationRequest(
                TenantId: invitation.TenantId,
                Kind: NotificationKinds.InvitationExpired,
                Severity: NotificationSeverity.Warning,
                RequiredPermission: PlatformPermissions.MembersManage,
                TitleKey: NotificationKinds.TitleKey(NotificationKinds.InvitationExpired),
                BodyKey: NotificationKinds.BodyKey(NotificationKinds.InvitationExpired),
                // Keyed on the row *and* the deadline that lapsed, not a fresh guid. Resend
                // rolls ExpiresAt and clears ExpiredNotifiedAt, so the same invitation can
                // legitimately lapse more than once; including the deadline lets the second
                // lapse notify while still collapsing a redelivery of the first.
                DedupeKey: $"invitation.expired:{invitation.Id:N}:{invitation.ExpiresAt.UtcTicks}",
                Params: new { email = invitation.Email },
                LinkPath: "/members",
                ResourceType: "invitation",
                ResourceId: invitation.Id), ct);
        }

        logger.LogInformation("Reported {Count} expired invitation(s).", due.Count);
    }
}
