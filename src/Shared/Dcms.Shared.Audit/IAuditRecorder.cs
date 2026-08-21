namespace Dcms.Shared.Audit;

/// <summary>
/// How every caller records an action. Scoped to one request or one consumed message.
///
/// <para><b>Recording never throws and never blocks on I/O.</b> A business change that has
/// already committed must not be undone because logging had a bad moment, and an endpoint
/// must not slow down because a downstream is unhealthy. Entries are buffered and handed to
/// the sink at flush.</para>
/// </summary>
public interface IAuditRecorder
{
    /// <summary>
    /// Starts an entry, adds it to the buffer, and returns it so the caller can fill in the
    /// resource and metadata fluently:
    /// <code>audit.Record(AuditActions.SiteDeleted).For("site", id, name).With("builds", n);</code>
    /// </summary>
    AuditEntry Record(string action);

    /// <summary>Adds an already-composed entry.</summary>
    void Record(AuditEntry entry);

    /// <summary>
    /// The entry <c>.WithAudit(...)</c> opened for this request, before the handler ran.
    ///
    /// <para>Enrich it rather than recording a second entry, so the endpoint produces one
    /// record carrying everything known about the action:
    /// <code>audit.Declared?.For("site", site.Id, site.Name).With("builds", removed);</code>
    /// It is already in the buffer, so anything added here rides the same transaction as the
    /// change — including the field diff the EF layer attaches to it.</para>
    ///
    /// <para>Null on paths with no declaring endpoint: a consumer, a worker, or a handler
    /// reached outside the endpoint filter.</para>
    /// </summary>
    AuditEntry? Declared { get; }

    /// <summary>
    /// Restates what the endpoint's record is, for a handler whose action is only known once
    /// it has run — a sign-in that could be a success, a failure or a lockout, all on one
    /// route. Retargets <see cref="Declared"/> in place, so the endpoint still produces one
    /// record; opens a fresh entry when there is no declaring endpoint.
    /// </summary>
    AuditEntry Declare(string action);

    /// <summary>
    /// Withdraws a buffered entry that has not been written yet. Exists for one case: the
    /// endpoint declared an action, the handler then failed or refused, and nothing was saved —
    /// recording an action that did not happen is worse than recording nothing. Does nothing
    /// once the entry has been drained, which is the correct outcome, because a drained entry
    /// is already enlisted in a transaction that committed.
    /// </summary>
    void Discard(AuditEntry entry);

    /// <summary>
    /// Writes one entry immediately rather than at flush, and <b>propagates failure</b>.
    /// For the handful of irreversible operations that must not proceed unrecorded — a
    /// tenant purge writes its intent this way before it starts destroying anything.
    /// </summary>
    ValueTask RecordNowAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Entries buffered so far in this scope.</summary>
    IReadOnlyList<AuditEntry> Pending { get; }

    /// <summary>
    /// Empties the buffer and returns the completed records, without writing them anywhere.
    /// Used by the EF interceptor, which enlists them into the transaction that is about to
    /// commit the change they describe — the caller takes ownership of persisting them, so
    /// this must not be called by anything that might then drop them.
    /// </summary>
    IReadOnlyList<AuditEvent> Drain();

    /// <summary>
    /// Completes every buffered entry with the ambient envelope and hands them to the sink.
    /// Called by the middleware at the end of a request, or by a consumer after handling a
    /// message. Safe to call more than once; the buffer is drained.
    /// </summary>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Where completed records go. The implementation is what differs between services:
/// those that own the audit schema insert into <c>audit.audit_outbox</c> inside the very
/// transaction that carries the business change, so a committed change cannot exist without
/// its record; those that cannot reach the schema (email-worker has no database, site-builder
/// is confined to the <c>sites</c> schema) hand off to a channel that forwards to JetStream.
/// </summary>
public interface IAuditSink
{
    ValueTask WriteAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken = default);
}
