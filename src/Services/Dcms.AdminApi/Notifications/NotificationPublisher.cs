using Dcms.Shared.Data.Notifications;
using Dcms.Shared.Data.Tenancy;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Dcms.AdminApi.Notifications;

/// <param name="RequiredPermission">
/// The permission a member must hold to receive this. Always the key that already gates the
/// underlying feature, so a notification cannot reveal something its recipient could not open.
/// </param>
/// <param name="DedupeKey">
/// Identifies the underlying <b>fact</b>, and is unique per tenant.
///
/// <para>Not the source event's id, which is the mistake this comment exists to prevent. An
/// event id identifies a <i>publish</i>, and a producer that announces the same fact twice
/// mints a fresh one each time — so keying on it deduplicates redelivery and nothing else.
/// That is precisely what happened: a site build outlived its JetStream ack deadline, ran
/// three times, published <c>site.published</c> three times with three event ids, and put
/// three identical rows in one admin's bell.</para>
///
/// <para>Key on the thing the notification is <i>about</i> — the build id, the asset id, the
/// item plus the moment it was published. The test is: if this arrived twice, would the
/// recipient consider it the same piece of news?</para>
/// </param>
/// <param name="ExtraUserIds">
/// Recipients to include regardless of permission, for notifications with a specific
/// addressee (the inviter, when their invitation is accepted).
/// </param>
public sealed record NotificationRequest(
    Guid TenantId,
    string Kind,
    NotificationSeverity Severity,
    string RequiredPermission,
    string TitleKey,
    string BodyKey,
    string DedupeKey,
    object? Params = null,
    string? LinkPath = null,
    string? ResourceType = null,
    Guid? ResourceId = null,
    Guid? ActorUserId = null,
    IReadOnlyCollection<Guid>? ExtraUserIds = null);

public interface INotificationPublisher
{
    /// <summary>
    /// Resolves recipients, persists the notification, and pushes it to whoever is connected.
    /// Returns the number of recipients, or 0 when the notification was a duplicate or had
    /// no audience. Never throws for an ordinary failure — callers raise notifications
    /// alongside business writes that must not be lost to a notification problem.
    /// </summary>
    Task<int> RaiseAsync(NotificationRequest request, CancellationToken ct = default);
}

public sealed class NotificationPublisher(
    NotificationsDbContext db,
    TenancyDbContext tenancy,
    IHubContext<NotificationHub> hub,
    ILogger<NotificationPublisher> logger) : INotificationPublisher
{
    public async Task<int> RaiseAsync(NotificationRequest request, CancellationToken ct = default)
    {
        try
        {
            return await RaiseCoreAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A notification is a courtesy on top of a change that has already happened.
            // Letting this bubble would roll back or fail the caller's real work.
            logger.LogWarning(ex, "Raising notification {Kind} for tenant {Tenant} failed.",
                request.Kind, request.TenantId);
            return 0;
        }
    }

    private async Task<int> RaiseCoreAsync(NotificationRequest request, CancellationToken ct)
    {
        var recipients = await ResolveRecipientsAsync(request, ct);
        if (recipients.Count == 0)
        {
            // Not an error: a tenant can legitimately have nobody holding the gating
            // permission. Recorded at Debug so a "why did nobody get told" question is
            // answerable without turning the log into noise.
            logger.LogDebug("Notification {Kind} for tenant {Tenant} has no recipients.",
                request.Kind, request.TenantId);
            return 0;
        }

        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            Kind = request.Kind,
            Severity = request.Severity,
            TitleKey = request.TitleKey,
            BodyKey = request.BodyKey,
            ParamsJson = SerializeParams(request.Params),
            LinkPath = request.LinkPath,
            ResourceType = request.ResourceType,
            ResourceId = request.ResourceId,
            ActorUserId = request.ActorUserId,
            DedupeKey = request.DedupeKey,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var rows = recipients.Select(userId => new NotificationRecipient
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            NotificationId = notification.Id,
            UserId = userId,
            CreatedAt = notification.CreatedAt,
        }).ToList();

        db.Notifications.Add(notification);
        db.Recipients.AddRange(rows);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The (TenantId, DedupeKey) index fired: this occurrence was already notified,
            // by a redelivery of the same JetStream message or a racing replica. Both are
            // expected under at-least-once delivery, so this is a success, not a failure.
            db.ChangeTracker.Clear();
            logger.LogDebug("Notification {Kind} for tenant {Tenant} already raised ({Key}).",
                request.Kind, request.TenantId, request.DedupeKey);
            return 0;
        }

        await PushAsync(notification, rows, ct);
        return rows.Count;
    }

    /// <summary>
    /// Members of the tenant whose roles grant <c>RequiredPermission</c>, plus any explicit
    /// extras. This is the inverse of <see cref="Tenancy.TenancyPermissionResolver"/>'s
    /// query, and it runs with <c>IgnoreQueryFilters</c> naming the tenant explicitly because
    /// the usual caller is a background consumer with no ambient tenant.
    /// </summary>
    private async Task<List<Guid>> ResolveRecipientsAsync(NotificationRequest request, CancellationToken ct)
    {
        var tenantId = request.TenantId;

        var roleIds = tenancy.TenantRolePermissions.IgnoreQueryFilters()
            .Where(p => p.TenantId == tenantId && p.Permission == request.RequiredPermission)
            .Select(p => p.TenantRoleId);

        var permitted = await tenancy.Memberships.AsNoTracking().IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.Roles.Any(mr => roleIds.Contains(mr.TenantRoleId)))
            .Select(m => m.UserId)
            .ToListAsync(ct);

        if (request.ExtraUserIds is { Count: > 0 })
        {
            // Extras still have to be members — an addressee who has since left the tenant
            // must not keep receiving its notifications.
            var extras = request.ExtraUserIds.Distinct().ToArray();
            var stillMembers = await tenancy.Memberships.AsNoTracking().IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && extras.Contains(m.UserId))
                .Select(m => m.UserId)
                .ToListAsync(ct);
            permitted.AddRange(stillMembers);
        }

        return permitted.Distinct().ToList();
    }

    private async Task PushAsync(
        Notification notification, List<NotificationRecipient> rows, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            var dto = NotificationDto.From(notification, row);
            try
            {
                await hub.Clients
                    .Group(NotificationHub.UserGroup(notification.TenantId, row.UserId))
                    .SendAsync("Notification", dto, ct);
            }
            catch (Exception ex)
            {
                // The row is committed; the badge corrects itself on the next fetch or
                // reconnect. Losing the live push is not worth losing the notification.
                logger.LogWarning(ex, "Pushing notification {Id} to user {User} failed.",
                    notification.Id, row.UserId);
            }
        }
    }

    private static string SerializeParams(object? value) => value switch
    {
        null => "{}",
        // Already JSON, from an inbound notify.raise. Round-tripping it would reorder keys
        // and silently drop anything the sender's shape does not model here.
        RawJson raw => raw.Json,
        _ => System.Text.Json.JsonSerializer.Serialize(value),
    };

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
