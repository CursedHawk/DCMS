using System.Text.Json;
using Dcms.Shared.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Dcms.Shared.Data.Audit;

/// <summary>
/// Enlists buffered audit records into the transaction that is committing the change they
/// describe, after giving <see cref="AuditChangeCapture"/> the chance to attach a field diff.
///
/// <para>On every save, whatever the recorder has buffered is drained into the *saving*
/// context's change tracker as <see cref="AuditOutboxMessage"/> rows. They then commit — or
/// roll back — with the business change, atomically. This is the whole reason the write path
/// is an outbox rather than a message publish.</para>
///
/// <para><b>The one call-site convention this imposes: record before you save.</b>
/// <c>audit.Record(...)</c> followed by <c>db.SaveChangesAsync()</c> is atomic; the reverse
/// order leaves the entry for the end-of-request flush, which writes it a moment later
/// through <see cref="AuditDbContext"/> and is therefore not atomic. Both record; only the
/// first is durable against a crash in between. <c>.WithAudit(...)</c> satisfies the
/// convention for free, by opening the entry before the handler runs.</para>
///
/// <para>Registered in DI as <c>IInterceptor</c> and attached to each context by
/// <c>UseDcmsAuditInterceptors</c>. Registering it is <b>not</b> sufficient on its own — EF
/// Core does not pull interceptors out of the application container by itself, and a context
/// that misses the attaching line commits changes and records none of them without complaint.
/// <c>AuditInterceptorDiscoveryTests</c> saves through every context and asserts the buffer
/// drained, because the failure mode is silence.</para>
/// </summary>
public sealed class AuditOutboxInterceptor(
    IAuditRecorder recorder,
    AuditChangeCapture capture,
    AuditEventSerializer serializer) : SaveChangesInterceptor
{
    /// <summary>
    /// The rows this interceptor added to the current save, so they can be withdrawn if it
    /// fails. Per-context, because one scope may save through several.
    /// </summary>
    private readonly Dictionary<DbContext, List<AuditOutboxMessage>> _enlisted = [];

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Enlist(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Enlist(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Forget(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Forget(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Withdraw(eventData.Context);

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Withdraw(eventData.Context);
        return Task.CompletedTask;
    }

    private void Enlist(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        // A context that does not map the outbox (a least-privilege worker's context, or one
        // deliberately left out) must not be handed rows it cannot write — leave the entries
        // buffered so the end-of-request flush deals with them, and do not bother diffing.
        if (context.Model.FindEntityType(typeof(AuditOutboxMessage)) is null)
        {
            return;
        }

        // Before the drain, because the diff belongs on the entry it describes and the drain
        // is what freezes an entry into an immutable record.
        capture.Capture(context);

        if (recorder.Pending.Count == 0)
        {
            return;
        }

        var rows = new List<AuditOutboxMessage>();
        foreach (var @event in recorder.Drain())
        {
            var row = new AuditOutboxMessage
            {
                EventId = @event.EventId,
                OccurredAt = @event.OccurredAt,
                PayloadJson = serializer.Serialize(@event),
            };
            context.Add(row);
            rows.Add(row);
        }

        _enlisted[context] = rows;
    }

    private void Forget(DbContext? context)
    {
        if (context is not null)
        {
            _enlisted.Remove(context);
        }
    }

    /// <summary>
    /// Detaches the rows added for a save that threw.
    ///
    /// <para>Nothing was committed, so nothing should be recorded — and the entries have
    /// already left the recorder's buffer, so without this they would sit in the change
    /// tracker in <c>Added</c> state and be inserted by whatever saved next. A retry of the
    /// same work would then collide on <c>EventId</c>, and a caller that recovered and did
    /// something else entirely would have written the record for the thing that failed.</para>
    /// </summary>
    private void Withdraw(DbContext? context)
    {
        if (context is null || !_enlisted.Remove(context, out var rows))
        {
            return;
        }

        foreach (var row in rows)
        {
            context.Entry(row).State = EntityState.Detached;
        }
    }
}

/// <summary>
/// Serialises records for the outbox and for the wire. Kept as a service rather than a static
/// helper so the options live in one place: the payload is read back by the chain writer, and
/// a mismatch between how it is written and how it is read would corrupt the chain silently.
/// </summary>
public sealed class AuditEventSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    public string Serialize(AuditEvent @event) => JsonSerializer.Serialize(@event, Options);

    public AuditEvent? Deserialize(string json) => JsonSerializer.Deserialize<AuditEvent>(json, Options);
}
