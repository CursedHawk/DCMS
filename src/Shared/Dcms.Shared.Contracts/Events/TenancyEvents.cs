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

public sealed record MembershipChanged(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid UserId) : IDcmsEvent
{
    public int Version => 1;
}
