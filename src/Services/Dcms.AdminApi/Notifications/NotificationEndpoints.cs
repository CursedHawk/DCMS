using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// The bell's REST surface.
///
/// <para><b>No <c>RequirePermission</c> anywhere here, deliberately.</b> The audience was
/// already decided when each notification was raised — a recipient row exists only for
/// someone who held the gating permission at that moment. Re-checking a permission at read
/// time would be both redundant and wrong: it would hide notifications from a member whose
/// role changed after the fact, and it would need a permission key this endpoint cannot know,
/// since one list mixes many kinds. What every query does instead, without exception, is
/// filter on the caller's own <c>UserId</c>.</para>
///
/// <para><b>Why the three writes are audit-exempt and suppress bulk capture.</b> They use
/// <c>ExecuteUpdateAsync</c>, which leaves no before-image, so the command interceptor would
/// otherwise record a bare <c>data.bulk.updated</c> — "a table got shorter" — for every time an
/// admin opened the bell. Read state is per-user UI state scoped to a single row the caller
/// owns: it changes nothing about the tenant, and the action the notification describes was
/// already audited where it happened. Recording it would bury the audit log in noise without
/// answering any question anyone will ask of it.</para>
/// </summary>
public static class NotificationEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    public static void MapNotificationEndpoints(this WebApplication app)
    {
        // The bell's only call on open: a page plus the badge number in one round trip.
        app.MapGet("/api/admin/notifications", async (
            NotificationsDbContext db, CurrentUser me,
            string? cursor, bool? unreadOnly, int? limit, CancellationToken ct) =>
        {
            var userId = me.RequireUserId();
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

            var query = db.Recipients.AsNoTracking()
                .Where(r => r.UserId == userId && r.DismissedAt == null);

            if (unreadOnly == true)
            {
                query = query.Where(r => r.ReadAt == null);
            }

            // Keyset pagination on CreatedAt: an offset would skip or repeat rows as new
            // notifications arrive at the head of the very list being paged.
            if (!string.IsNullOrWhiteSpace(cursor) &&
                DateTimeOffset.TryParse(cursor, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var before))
            {
                query = query.Where(r => r.CreatedAt < before);
            }

            var rows = await query
                .OrderByDescending(r => r.CreatedAt)
                .Take(take + 1)
                .Join(db.Notifications.AsNoTracking(), r => r.NotificationId, n => n.Id,
                    (r, n) => new { Recipient = r, Notification = n })
                .ToListAsync(ct);

            var hasMore = rows.Count > take;
            var page = rows.Take(take).ToList();

            var unread = await db.Recipients.AsNoTracking()
                .CountAsync(r => r.UserId == userId && r.ReadAt == null && r.DismissedAt == null, ct);

            return Results.Ok(new NotificationPageDto(
                page.Select(x => NotificationDto.From(x.Notification, x.Recipient)).ToList(),
                unread,
                hasMore ? page[^1].Recipient.CreatedAt.ToString("O") : null));
        }).RequireAuthorization();

        // Split out so the badge can refresh without paying for a page of rows. Cheap: it is
        // a covering count on IX(TenantId, UserId, ReadAt, CreatedAt).
        app.MapGet("/api/admin/notifications/unread-count", async (
            NotificationsDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var userId = me.RequireUserId();
            var unread = await db.Recipients.AsNoTracking()
                .CountAsync(r => r.UserId == userId && r.ReadAt == null && r.DismissedAt == null, ct);
            return Results.Ok(new { unread });
        }).RequireAuthorization();

        app.MapGet("/api/admin/notifications/{id:guid}", async (
            Guid id, NotificationsDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            var userId = me.RequireUserId();
            var row = await db.Recipients.AsNoTracking()
                .Where(r => r.NotificationId == id && r.UserId == userId)
                .Join(db.Notifications.AsNoTracking(), r => r.NotificationId, n => n.Id,
                    (r, n) => new { Recipient = r, Notification = n })
                .FirstOrDefaultAsync(ct);

            // 404 rather than 403 for someone else's notification: 403 would confirm it
            // exists, which is the same reasoning GET /audit/{id} uses.
            return row is null
                ? Results.NotFound()
                : Results.Ok(NotificationDto.From(row.Notification, row.Recipient));
        }).RequireAuthorization();

        app.MapPost("/api/admin/notifications/{id:guid}/read", async (
            Guid id, NotificationsDbContext db, CurrentUser me, AuditScope audit,
            CancellationToken ct) =>
        {
            var userId = me.RequireUserId();
            var now = DateTimeOffset.UtcNow;
            using var suppressed = audit.SuppressBulkCapture();

            // Already-read is success, not 404: the bell marks on open and the user may also
            // click, so a second call is an ordinary race rather than a mistake. The
            // TenantId query filter plus the UserId predicate mean this can only ever touch
            // the caller's own row.
            await db.Recipients
                .Where(r => r.NotificationId == id && r.UserId == userId && r.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReadAt, now), ct);

            return Results.NoContent();
        }).RequireAuthorization()
          .AuditExempt("Marking one's own notification read is a per-user UI state change, "
                     + "not a change to tenant state, and the underlying action is already audited.");

        app.MapPost("/api/admin/notifications/read-all", async (
            NotificationsDbContext db, CurrentUser me, AuditScope audit,
            CancellationToken ct) =>
        {
            var userId = me.RequireUserId();
            var now = DateTimeOffset.UtcNow;
            using var suppressed = audit.SuppressBulkCapture();

            var updated = await db.Recipients
                .Where(r => r.UserId == userId && r.ReadAt == null && r.DismissedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReadAt, now), ct);
            return Results.Ok(new { updated });
        }).RequireAuthorization()
          .AuditExempt("Marking one's own notifications read is a per-user UI state change.");

        app.MapPost("/api/admin/notifications/{id:guid}/dismiss", async (
            Guid id, NotificationsDbContext db, CurrentUser me, AuditScope audit,
            CancellationToken ct) =>
        {
            var userId = me.RequireUserId();
            var now = DateTimeOffset.UtcNow;
            using var suppressed = audit.SuppressBulkCapture();

            // Dismissing implies read: a dismissed row must never keep inflating the badge.
            await db.Recipients
                .Where(r => r.NotificationId == id && r.UserId == userId && r.DismissedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.DismissedAt, now)
                    .SetProperty(r => r.ReadAt, r => r.ReadAt ?? now), ct);
            return Results.NoContent();
        }).RequireAuthorization()
          .AuditExempt("Dismissing one's own notification is a per-user UI state change.");
    }
}
