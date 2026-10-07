namespace Dcms.Shared.Contracts.Events;

/// <summary>An account in a tenant's realm became active (<see cref="Messaging.Subjects.RealmUserActivated"/>).</summary>
public sealed record RealmUserActivated(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid TenantId,
    Guid UserId,
    string Email) : ITenantEvent
{
    public int Version => 1;
}
