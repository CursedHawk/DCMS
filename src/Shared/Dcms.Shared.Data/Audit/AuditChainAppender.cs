using System.Text.Json;
using Dcms.Shared.Audit;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Audit;

/// <summary>
/// Appends records to their chain. The only thing in the system that writes
/// <c>audit.audit_events</c>.
///
/// <para><b>Serialisation is by row lock, not by deployment topology.</b> It would be tempting
/// to say "there is one writer, so appends are ordered" — but admin-api can run more than one
/// replica, and a durable JetStream consumer is shared across them. Taking
/// <c>SELECT … FOR UPDATE</c> on the chain head is what actually orders the chain, and it costs
/// nothing when there genuinely is only one writer.</para>
///
/// <para><b>Deduplication and the head bump are one transaction</b> — the caller's, if it
/// already has one open. Split apart they race:
/// two rows end up claiming one <c>Seq</c>, or the head advances for a row that rolled back.
/// Because the existence check happens under the head lock, and an event belongs to exactly one
/// chain, check-then-insert is sound here — the unique index remains as a backstop.</para>
/// </summary>
public sealed class AuditChainAppender(
    AuditDbContext db,
    IAuditChainKeyProvider keys,
    IClock clock)
{
    /// <summary>
    /// Appends a batch in one transaction, skipping any record already present.
    /// Returns how many were newly written.
    /// </summary>
    public async Task<int> AppendAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
        {
            return 0;
        }

        var key = keys.GetKey();
        var written = 0;

        // Join the caller's transaction when there is one. The drain claims its outbox rows
        // FOR UPDATE and hands them here on the same context, so opening a second transaction
        // on that connection throws — and even if it could, committing the append separately
        // would leave a window where records are chained but their outbox rows are unclaimed.
        // One transaction, committed by whoever opened it.
        var ambient = db.Database.CurrentTransaction;
        await using var owned = ambient is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        foreach (var @event in events)
        {
            if (await AppendOneAsync(@event, key, cancellationToken))
            {
                written++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        if (owned is not null)
        {
            await owned.CommitAsync(cancellationToken);
        }

        return written;
    }

    private async Task<bool> AppendOneAsync(AuditEvent @event, byte[] key, CancellationToken cancellationToken)
    {
        var chainKey = @event.TenantId;

        // Every use of this instant below — the period, the dedup probe, the row, and through
        // the row the hash — must be the value Postgres will actually hold. See StorablePrecision.
        var occurredAt = StorablePrecision(@event.OccurredAt);
        var period = PeriodOf(occurredAt);

        // Flush anything already staged before taking the lock, so the existence check below
        // sees a consistent picture and the lock is held for as short a time as possible.
        await db.SaveChangesAsync(cancellationToken);

        var head = await LockHeadAsync(chainKey, period, cancellationToken);

        var exists = await db.Events
            .AsNoTracking()
            .AnyAsync(e => e.OccurredAt == occurredAt && e.EventId == @event.EventId, cancellationToken);
        if (exists)
        {
            // A redelivery or a retried drain. Idempotent by design: the chain must not gain a
            // second copy, and the head must not advance for one.
            return false;
        }

        var row = ToRow(@event, chainKey, period, head.Seq + 1, occurredAt);
        row.PrevHash = head.Seq == 0 ? null : head.LastHash;
        row.HashVersion = AuditCanonicalizer.CurrentVersion;
        row.Hash = AuditCanonicalizer.ComputeHash(row, row.PrevHash, key);

        db.Events.Add(row);

        head.Seq = row.Seq;
        head.LastHash = row.Hash;
        head.UpdatedAt = clock.UtcNow;

        return true;
    }

    /// <summary>
    /// Reads the chain head with a row lock, creating it on first use for this
    /// (tenant, month). A brand-new head has <c>Seq = 0</c>, so the first real record is 1.
    /// </summary>
    private async Task<AuditChainHead> LockHeadAsync(Guid chainKey, DateOnly period, CancellationToken cancellationToken)
    {
        var existing = await db.ChainHeads
            .FromSql($"""
                SELECT * FROM audit.chain_heads
                WHERE "ChainKey" = {chainKey} AND "Period" = {period}
                FOR UPDATE
                """)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var head = new AuditChainHead
        {
            ChainKey = chainKey,
            Period = period,
            Seq = 0,
            LastHash = [],
            UpdatedAt = clock.UtcNow,
        };
        db.ChainHeads.Add(head);
        await db.SaveChangesAsync(cancellationToken);
        return head;
    }

    /// <summary>
    /// The instant as Postgres will store it: UTC, truncated to microseconds.
    ///
    /// <para><b>The chain hash covers OccurredAt, so this has to happen before the hash is
    /// computed.</b> A .NET <see cref="DateTimeOffset"/> counts 100-nanosecond ticks and
    /// renders seven fractional digits; <c>timestamptz</c> holds microseconds and keeps six.
    /// Hashing the seven-digit value and then verifying against the six-digit value Postgres
    /// handed back made records fail with "this row was altered after it was written" — a
    /// tampering alarm raised by a rounding difference.</para>
    ///
    /// <para>Worse than merely wrong: it was <i>intermittent</i>. A timestamp whose seventh
    /// digit happened to be zero survived intact, so some records verified and others did not,
    /// which looks far more like selective tampering than a systematic fault does.</para>
    ///
    /// <para>Truncate, never round: rounding can carry into the next microsecond and move a
    /// record across a partition boundary at the edge of a month.</para>
    /// </summary>
    public static DateTimeOffset StorablePrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    /// <summary>
    /// The month a record belongs to, in UTC. Part of the chain identity, so that each monthly
    /// partition holds a self-contained chain and retention can drop one without breaking any
    /// chain that outlives it.
    /// </summary>
    public static DateOnly PeriodOf(DateTimeOffset occurredAt)
    {
        var utc = occurredAt.ToUniversalTime();
        return new DateOnly(utc.Year, utc.Month, 1);
    }

    /// <summary>
    /// How the two jsonb payloads are written. camelCase because these columns are handed to
    /// the browser verbatim — the read plane returns the stored text rather than reshaping it,
    /// so what is written here is what the audit page reads, and it should look like every
    /// other payload the API serves.
    ///
    /// <para>Changing this changes what future rows hash over. That is safe — a sealed row
    /// verifies against the text it was written with — but it is not a free edit: do it
    /// knowingly, and bump <c>AuditCanonicalizer.CurrentVersion</c> if the meaning changes
    /// rather than only the spelling.</para>
    /// </summary>
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static AuditEventRow ToRow(
        AuditEvent e, Guid chainKey, DateOnly period, long seq, DateTimeOffset occurredAt) => new()
    {
        Id = Guid.CreateVersion7(),
        EventId = e.EventId,
        // Already reduced to what timestamptz can hold — see StorablePrecision. Passed in
        // rather than recomputed so the value the caller deduped on is the value stored.
        OccurredAt = occurredAt,
        // Not covered by the hash, so its precision does not matter to verification.
        RecordedAt = DateTimeOffset.UtcNow,

        ChainKey = chainKey,
        Period = period,
        Seq = seq,

        TenantId = e.TenantId,
        IsSandbox = e.IsSandbox,

        Action = e.Action,
        Category = e.Category.ToString().ToLowerInvariant(),
        Outcome = e.Outcome.ToString().ToLowerInvariant(),
        Severity = (short)e.Severity,

        ActorKind = e.Actor.Kind.ToString().ToLowerInvariant(),
        ActorId = e.Actor.Id,
        ActorRef = e.Actor.Ref,
        ActorDisplay = e.Actor.Display,
        ActorAttribution = e.Actor.Attribution.ToString().ToLowerInvariant(),
        SubjectUserId = e.SubjectUserId,

        ResourceType = e.ResourceType,
        ResourceId = e.ResourceId,
        ResourceLabel = e.ResourceLabel,

        ServiceName = e.ServiceName,
        ServiceInstance = e.ServiceInstance,
        ProducerSeq = e.ProducerSeq,

        CorrelationId = e.CorrelationId,
        CausationId = e.CausationId,
        TraceId = e.TraceId,
        SpanId = e.SpanId,

        HttpMethod = e.Http?.Method,
        RoutePattern = e.Http?.RoutePattern,
        StatusCode = e.Http?.StatusCode,
        IpAddress = e.Http?.IpAddress,
        IpTrusted = e.Http?.IpTrusted ?? false,
        UserAgent = e.Http?.UserAgent,

        MetadataJson = JsonSerializer.Serialize(e.Metadata, StoredJson),
        ChangesJson = e.Changes is null ? null : JsonSerializer.Serialize(e.Changes, StoredJson),
        RedactionVersion = (short)e.RedactionVersion,
        SchemaVersion = (short)e.SchemaVersion,
    };
}
