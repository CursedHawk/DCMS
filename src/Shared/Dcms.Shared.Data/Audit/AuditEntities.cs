using Dcms.Shared.Audit.Redaction;
namespace Dcms.Shared.Data.Audit;

/// <summary>
/// One persisted audit record.
///
/// <para><b>Deliberately not a <see cref="TenantEntity"/>.</b> Deriving from it would pull in
/// the per-context <c>HasQueryFilter(x =&gt; x.TenantId == CurrentTenantId)</c> convention, and
/// the chain writer runs as a background service with no ambient tenant — the filter would
/// resolve to <see cref="Guid.Empty"/> and hide every tenant's rows from the very process
/// that has to read them. Tenant scoping is enforced explicitly in the read-plane query
/// instead, where it is visible.</para>
///
/// <para><see cref="TenantId"/> is never null: <see cref="Guid.Empty"/> is the platform scope
/// (a login, a tenant being provisioned). The RLS policy compares
/// <c>"TenantId" = current_setting(...)::uuid</c>, and <c>NULL = x</c> yields NULL rather than
/// TRUE, so a null would hide the row from every RLS-constrained reader — not just from
/// other tenants.</para>
/// </summary>
public sealed class AuditEventRow
{
    public Guid Id { get; set; }

    /// <summary>Producer-assigned id. With <see cref="OccurredAt"/>, the idempotency key.</summary>
    public Guid EventId { get; set; }

    /// <summary>
    /// Producer clock. Partition key, and half the dedup key — so it must be carried
    /// unchanged through every retry and redelivery, never re-stamped at write time.
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    // ---- chain ----
    /// <summary>The tenant whose chain this belongs to; <see cref="Guid.Empty"/> for platform scope.</summary>
    public Guid ChainKey { get; set; }

    /// <summary>
    /// Month bucket, and part of the chain identity. One chain per tenant per month means each
    /// monthly partition is self-contained, so retention can drop a whole partition without
    /// breaking any chain that outlives it.
    /// </summary>
    public DateOnly Period { get; set; }

    /// <summary>
    /// Position within <c>(ChainKey, Period)</c>. This is <b>writer-arrival order, not event
    /// order</b> — verify by Seq, display by <see cref="OccurredAt"/>, and never assume the two
    /// agree.
    /// </summary>
    public long Seq { get; set; }

    /// <summary>Null only for the first record of a chain.</summary>
    public byte[]? PrevHash { get; set; }

    public byte[] Hash { get; set; } = [];

    /// <summary>Lets the hash algorithm evolve without invalidating already-sealed segments.</summary>
    public short HashVersion { get; set; }

    // ---- scope ----
    public Guid TenantId { get; set; }
    public bool IsSandbox { get; set; }

    // ---- semantics ----
    public string Action { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public short Severity { get; set; }

    // ---- actor ----
    public string ActorKind { get; set; } = string.Empty;

    /// <summary>Null whenever the identity is not a Guid — client-credentials, webhooks, workers.</summary>
    public Guid? ActorId { get; set; }

    public string? ActorRef { get; set; }
    public string? ActorDisplay { get; set; }

    /// <summary>direct / propagated / inferred — see AuditAttribution.</summary>
    public string ActorAttribution { get; set; } = string.Empty;

    /// <summary>The user the event is *about*, when that differs from the actor.</summary>
    public Guid? SubjectUserId { get; set; }

    // ---- resource ----
    public string? ResourceType { get; set; }

    /// <summary>Text, not a Guid: some resources are keyed by slug, hostname or file path.</summary>
    public string? ResourceId { get; set; }

    public string? ResourceLabel { get; set; }

    // ---- provenance ----
    public string ServiceName { get; set; } = string.Empty;
    public string ServiceInstance { get; set; } = string.Empty;

    /// <summary>Per-instance monotonic counter; makes *omissions* detectable, which no chain can.</summary>
    public long ProducerSeq { get; set; }

    public string? CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }

    // ---- http ambient ----
    public string? HttpMethod { get; set; }
    public string? RoutePattern { get; set; }
    public int? StatusCode { get; set; }
    public string? IpAddress { get; set; }

    /// <summary>False when the address came from a forwarding header we accept from any proxy.</summary>
    public bool IpTrusted { get; set; }

    public string? UserAgent { get; set; }

    // ---- payload ----
    public string MetadataJson { get; set; } = "{}";
    public string? ChangesJson { get; set; }
    public short RedactionVersion { get; set; }
    public short SchemaVersion { get; set; }
}

/// <summary>
/// The tip of one chain. Locked <c>FOR UPDATE</c> by the writer so appends serialise on a row
/// rather than on deployment topology — admin-api may run more than one replica.
/// </summary>
public sealed class AuditChainHead
{
    public Guid ChainKey { get; set; }
    public DateOnly Period { get; set; }
    public long Seq { get; set; }
    public byte[] LastHash { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A sealed monthly segment. Never partitioned and never dropped by retention: this is what
/// outlives the rows it summarises, so that a dropped partition can still be shown to have
/// been intact, and a wholesale table rewrite remains detectable.
/// </summary>
public sealed class AuditChainAnchor
{
    public Guid ChainKey { get; set; }
    public DateOnly Period { get; set; }
    public long FirstSeq { get; set; }
    public long LastSeq { get; set; }
    public byte[] FirstHash { get; set; } = [];
    public byte[] LastHash { get; set; } = [];
    public long RowCount { get; set; }
    public DateTimeOffset SealedAt { get; set; }

    /// <summary>HMAC over the segment summary, keyed from Vault — the part an attacker with database access cannot forge.</summary>
    public byte[] AnchorHmac { get; set; } = [];

    public DateTimeOffset? PartitionDroppedAt { get; set; }
}

/// <summary>
/// Transactional outbox row, written in the same transaction as the business change it
/// describes. That is what makes "the change committed but the audit did not" impossible
/// rather than merely unlikely.
///
/// <para><see cref="Id"/> is a bigint identity, not a Guid: the chain needs a total order, and
/// <c>ContentOutboxMessage</c>'s Guid id ordered by timestamp has ties and is exposed to clock
/// skew.</para>
/// </summary>
// The audit log carrying itself. Recording that a record was written is a loop, not a fact.
[AuditIgnore]
public sealed class AuditOutboxMessage
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string PayloadJson { get; set; } = "{}";

    /// <summary>Set once the record has been appended to the chain.</summary>
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>Set once the record has been fanned out to the AUDIT stream for external sinks.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public int Attempts { get; set; }
}
