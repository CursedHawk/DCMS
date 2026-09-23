namespace Dcms.Shared.Contracts.Events;

/// <summary>
/// Base contract for every NATS event. <see cref="Version"/> guards payload evolution:
/// consumers must tolerate unknown fields and reject unknown major versions.
/// </summary>
public interface IDcmsEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
    int Version { get; }
}

/// <summary>
/// An event about one tenant's data. The consumer bases act as that tenant for the database
/// while they handle it (ADR 0015), so a consumer that forgets its <c>TenantId</c> predicate
/// reads that tenant's rows and no one else's. Every event carrying a tenant implements this;
/// the ones that do not (email, platform notifications, certificate reissue) are not about a
/// tenant's rows.
/// </summary>
public interface ITenantEvent : IDcmsEvent
{
    Guid TenantId { get; }
}
