using Dcms.AdminApi.Tenancy;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Data.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// The platform console's bell.
///
/// <para><b>SuperAdmin, checked imperatively</b>, exactly as the platform certificate endpoints
/// next door: these are platform-wide facts, and no tenant-scoped permission should reach them.
/// This differs from the tenant bell, which carries no permission check at all — there the
/// audience was decided when each notification was raised and every query filters on the
/// caller's own id. Here there are no recipient rows to filter by, so the role check <i>is</i>
/// the audience.</para>
///
/// <para>Read state is created on first touch. A notification an operator has never acted on has
/// no row, which is what "unread" means — so the unread count is an anti-join, and the list is a
/// left join. Affordable because of the volume: this table gains a row when a certificate
/// changes state, not on every publish and upload.</para>
/// </summary>
public static class PlatformNotificationEndpoints
{
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;

    public sealed record PlatformNotificationDto(
        Guid Id,
        string Kind,
        string Severity,
        string ParamsJson,
        string? LinkPath,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ReadAt);

    public sealed record PlatformNotificationPageDto(
        IReadOnlyList<PlatformNotificationDto> Items,
        int UnreadCount,
        string? NextCursor);

    public static IEndpointRouteBuilder MapPlatformNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/platform/notifications", async (
            NotificationsDbContext db, CurrentUser me,
            string? cursor, bool? unreadOnly, int? limit, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }

            var userId = me.RequireUserId();
            var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

            // Left join onto this operator's own read state. GroupJoin + SelectMany with
            // DefaultIfEmpty is the shape EF translates to a LEFT JOIN; a navigation would need
            // a collection on the notification, which would then have to be filtered by user in
            // every query that touched it.
            var query =
                from n in db.PlatformNotifications.AsNoTracking()
                join r in db.PlatformNotificationReads.AsNoTracking().Where(r => r.UserId == userId)
                    on n.Id equals r.NotificationId into state
                from r in state.DefaultIfEmpty()
                where r == null || r.DismissedAt == null
                select new { Notification = n, Read = r };

            if (unreadOnly == true)
            {
                query = query.Where(x => x.Read == null || x.Read.ReadAt == null);
            }

            // Keyset pagination on CreatedAt, as the tenant bell does: an offset would skip or
            // repeat rows as new notifications arrive at the head of the list being paged.
            if (!string.IsNullOrWhiteSpace(cursor) &&
                DateTimeOffset.TryParse(cursor, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var before))
            {
                query = query.Where(x => x.Notification.CreatedAt < before);
            }

            var rows = await query
                .OrderByDescending(x => x.Notification.CreatedAt)
                .Take(take + 1)
                .ToListAsync(ct);

            var hasMore = rows.Count > take;
            var page = rows.Take(take).ToList();

            return Results.Ok(new PlatformNotificationPageDto(
                [.. page.Select(x => new PlatformNotificationDto(
                    x.Notification.Id,
                    x.Notification.Kind,
                    x.Notification.Severity.ToString(),
                    x.Notification.ParamsJson,
                    x.Notification.LinkPath,
                    x.Notification.CreatedAt,
                    x.Read?.ReadAt))],
                await UnreadCountAsync(db, userId, ct),
                hasMore ? page[^1].Notification.CreatedAt.ToString("O") : null));
        }).RequireAuthorization();

        // Split out so the badge can refresh without paying for a page of rows.
        app.MapGet("/api/admin/platform/notifications/unread-count", async (
            NotificationsDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }

            return Results.Ok(new { unread = await UnreadCountAsync(db, me.RequireUserId(), ct) });
        }).RequireAuthorization();

        app.MapPost("/api/admin/platform/notifications/{id:guid}/read", async (
            Guid id, NotificationsDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }

            await TouchAsync(db, id, me.RequireUserId(), read: true, dismiss: false, ct);
            return Results.NoContent();
        }).RequireAuthorization()
          .AuditExempt("Marking one's own notification read is per-user UI state, not a change "
                     + "to platform state; what the notification describes is audited where it happened.");

        app.MapPost("/api/admin/platform/notifications/read-all", async (
            NotificationsDbContext db, CurrentUser me, AuditScope audit, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }

            var userId = me.RequireUserId();
            var now = DateTimeOffset.UtcNow;

            // ExecuteUpdate leaves no before-image, so the command interceptor would otherwise
            // record a bare "data.bulk.updated" -- "a table got shorter" -- every time an
            // operator cleared their bell. What changed is one user's own read state; the events
            // the notifications describe are audited where they happened.
            using var suppressed = audit.SuppressBulkCapture();

            // Two steps, because unread here means "no row" as well as "row with no ReadAt":
            // an UPDATE alone would silently leave every never-touched notification unread and
            // the badge would not clear, which is the one thing this button exists to do.
            var updated = await db.PlatformNotificationReads
                .Where(r => r.UserId == userId && r.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReadAt, now), ct);

            var untouched = await db.PlatformNotifications.AsNoTracking()
                .Where(n => !db.PlatformNotificationReads.Any(r => r.NotificationId == n.Id && r.UserId == userId))
                .Select(n => n.Id)
                .ToListAsync(ct);

            foreach (var notificationId in untouched)
            {
                db.PlatformNotificationReads.Add(new PlatformNotificationRead
                {
                    NotificationId = notificationId,
                    UserId = userId,
                    ReadAt = now,
                });
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { updated = updated + untouched.Count });
        }).RequireAuthorization()
          .AuditExempt("Per-user UI state; see the single-notification read endpoint.");

        app.MapPost("/api/admin/platform/notifications/{id:guid}/dismiss", async (
            Guid id, NotificationsDbContext db, CurrentUser me, CancellationToken ct) =>
        {
            if (!me.IsSuperAdmin)
            {
                return Results.Forbid();
            }

            // Read as well as dismissed: a dismissal is an acknowledgement, and leaving it
            // unread would keep it in the badge while hiding it from the list that could clear it.
            await TouchAsync(db, id, me.RequireUserId(), read: true, dismiss: true, ct);
            return Results.NoContent();
        }).RequireAuthorization()
          .AuditExempt("Per-user UI state; dismissing hides one operator's copy and deletes nothing.");

        return app;
    }

    private static Task<int> UnreadCountAsync(NotificationsDbContext db, Guid userId, CancellationToken ct)
        => db.PlatformNotifications.AsNoTracking()
            .CountAsync(n => !db.PlatformNotificationReads
                .Any(r => r.NotificationId == n.Id && r.UserId == userId
                          && (r.ReadAt != null || r.DismissedAt != null)), ct);

    /// <summary>
    /// Upserts one operator's read state.
    ///
    /// <para>A unique violation is treated as success: two clicks, or a click racing the
    /// mark-all button, are ordinary and must not surface as an error on a bell.</para>
    /// </summary>
    private static async Task TouchAsync(
        NotificationsDbContext db, Guid notificationId, Guid userId, bool read, bool dismiss,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await db.PlatformNotificationReads
            .FirstOrDefaultAsync(r => r.NotificationId == notificationId && r.UserId == userId, ct);

        if (row is null)
        {
            // Only for a notification that exists: without this an unknown id would mint read
            // state for nothing, and the FK would fail on save with a 500 rather than a 404.
            if (!await db.PlatformNotifications.AnyAsync(n => n.Id == notificationId, ct))
            {
                return;
            }

            row = new PlatformNotificationRead { NotificationId = notificationId, UserId = userId };
            db.PlatformNotificationReads.Add(row);
        }

        row.ReadAt ??= read ? now : null;
        if (dismiss)
        {
            row.DismissedAt ??= now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }
}
