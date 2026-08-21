namespace Dcms.Shared.Audit;

/// <summary>
/// Identifies the emitting process and numbers everything it emits.
///
/// A hash chain proves that recorded rows were not altered. It says nothing about rows that
/// were never written — and *that* is the failure mode this system actually has: a buffer
/// dropped on a hard kill, a publish that never landed. <see cref="NextSequence"/> gives each
/// process a monotonic counter, so a verifier can spot a gap in what one instance produced
/// and say "records 4,101–4,118 from this instance are missing", which no chain can do.
///
/// Singleton: <see cref="Instance"/> is minted once at startup, so a restart deliberately
/// starts a new sequence rather than colliding with the previous process's numbering.
/// </summary>
public sealed class AuditServiceIdentity(string serviceName)
{
    private long _sequence;

    public string ServiceName { get; } = serviceName;

    /// <summary>Process identity for this run. Short, since it is stored on every row.</summary>
    public string Instance { get; } = Guid.NewGuid().ToString("N")[..16];

    public long NextSequence() => Interlocked.Increment(ref _sequence);
}
