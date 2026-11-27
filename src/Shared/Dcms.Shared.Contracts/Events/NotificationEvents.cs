namespace Dcms.Shared.Contracts.Events;

/// <summary>
/// A request to raise an in-app notification, published by a service that cannot write the
/// notifications schema itself. admin-api's ingest consumer resolves the recipients and does
/// the actual insert, so the schema keeps exactly one writer.
///
/// <para><b>This is a request, not a fact.</b> Unlike <c>site.published</c>, nothing has
/// happened by virtue of this message existing — the sender is asking for a notification
/// about something that happened elsewhere. That is why <see cref="DedupeKey"/> is the
/// sender's responsibility: it must identify the underlying occurrence, not this message, or
/// a redelivery notifies twice.</para>
/// </summary>
/// <param name="RequiredPermission">
/// The permission a tenant member must hold to receive this. Use the key that already gates
/// the underlying feature, so a notification can never reveal something its recipient could
/// not have opened anyway.
/// </param>
/// <param name="ParamsJson">
/// JSON object of i18n interpolation values. Prose is not carried: the admin SPA is
/// translated, and a rendered sentence would freeze one language into the record.
/// </param>
public sealed record NotificationRaiseRequested(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    string Kind,
    string Severity,
    string RequiredPermission,
    string TitleKey,
    string BodyKey,
    string ParamsJson,
    string DedupeKey,
    string? LinkPath = null,
    string? ResourceType = null,
    Guid? ResourceId = null,
    Guid? ActorUserId = null) : ITenantEvent
{
    public int Version => 1;
}

/// <summary>
/// <c>notify.changed</c>: tell the tenant's open consoles that a class of data changed
/// (<c>ResourceChanged</c>). <see cref="Tag"/> is already namespaced by the sender.
/// </summary>
public sealed record ConsoleChangeRequested(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    string Tag,
    string? Id) : ITenantEvent
{
    public int Version => 1;
}
