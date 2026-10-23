using Dcms.Shared.Audit;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Audit;

/// <summary>
/// Writes records to <c>audit.audit_outbox</c> through <see cref="AuditDbContext"/>.
///
/// <para>This is the <b>safety net, not the main path</b>. The main path is
/// <see cref="AuditOutboxInterceptor"/>, which enlists records into the very transaction that
/// commits the change they describe. What reaches this sink is whatever was recorded outside
/// that window — after the save, or by an endpoint that changed nothing in the database at all
/// (a permission denial, a login failure, a read worth recording). Those have no transaction to
/// join, so a separate insert a moment later is the correct and only option for them.</para>
///
/// <para>The distinction matters for exactly one class of record: an entry recorded *after* its
/// own <c>SaveChangesAsync</c> lands here, and a crash in between would lose it. That is why
/// the convention is to record before saving, and why the tests assert the ordering for the
/// operations where losing the record would matter.</para>
///
/// <para>The context is resolved when there is something to write, not with the sink: the recorder
/// that holds this sink is built for every request, and so was an AuditDbContext nobody used.</para>
/// </summary>
public sealed class OutboxAuditSink(IServiceProvider services, AuditEventSerializer serializer) : IAuditSink
{
    public async ValueTask WriteAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
        {
            return;
        }

        var db = services.GetRequiredService<AuditDbContext>();
        foreach (var @event in events)
        {
            db.Outbox.Add(new AuditOutboxMessage
            {
                EventId = @event.EventId,
                OccurredAt = @event.OccurredAt,
                PayloadJson = serializer.Serialize(@event),
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
