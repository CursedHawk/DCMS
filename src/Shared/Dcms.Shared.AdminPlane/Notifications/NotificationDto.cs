using Dcms.Shared.Data.Notifications;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// What the bell renders. <c>ParamsJson</c> travels as a raw JSON string and is parsed by
/// the client, because the values are i18n interpolation arguments whose shape differs per
/// <see cref="Kind"/> and which the server never needs to inspect.
/// </summary>
public sealed record NotificationDto(
    Guid Id,
    string Kind,
    string Severity,
    string TitleKey,
    string BodyKey,
    string ParamsJson,
    string? LinkPath,
    string? ResourceType,
    Guid? ResourceId,
    Guid? ActorUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? DismissedAt)
{
    public static NotificationDto From(Notification n, NotificationRecipient r) => new(
        n.Id, n.Kind, n.Severity.ToString(), n.TitleKey, n.BodyKey, n.ParamsJson,
        n.LinkPath, n.ResourceType, n.ResourceId, n.ActorUserId, n.CreatedAt, r.ReadAt, r.DismissedAt);
}

/// <summary>A page of notifications plus the badge number, so the bell needs one request.</summary>
public sealed record NotificationPageDto(
    IReadOnlyList<NotificationDto> Items,
    int UnreadCount,
    string? NextCursor);
