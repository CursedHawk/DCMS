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
