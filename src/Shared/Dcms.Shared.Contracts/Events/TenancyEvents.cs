namespace Dcms.Shared.Contracts.Events;

public sealed record TenantCreated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    string Slug,
    string Name) : IDcmsEvent
{
    public int Version => 1;
}

public sealed record TenantDomainVerified(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid DomainId,
    string Hostname) : IDcmsEvent
{
    public int Version => 1;
}

public sealed record PluginInstanceChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid InstanceId,
    string PluginId,
    PluginInstanceChangeKind Kind) : IDcmsEvent
{
    public int Version => 1;
}

public enum PluginInstanceChangeKind
{
    Created,
    Updated,
    Enabled,
    Disabled,
    Deleted,
}

/// <summary>
/// A tenant was suspended or resumed. Carries the slug as well as the id because site-host
/// keys its domain cache on neither — it needs the slug to log intelligibly, and an event a
/// consumer has to make a second call to understand is an event that will be misused.
/// </summary>
public sealed record TenantStatusChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    string Slug,
    bool Suspended) : IDcmsEvent
{
    public int Version => 1;
}

public sealed record MembershipChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid UserId) : IDcmsEvent
{
    public int Version => 1;
}
