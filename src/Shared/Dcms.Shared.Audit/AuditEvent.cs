using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Kernel.Abstractions;

namespace Dcms.Shared.Audit;

/// <summary>Broad classification, used to filter the log and to set retention priorities.</summary>
public enum AuditCategory
{
    /// <summary>Tenant state changed.</summary>
    TenantState,

    /// <summary>Authentication lifecycle: sign-in, sign-out, password, SSO, keys.</summary>
    Auth,

    /// <summary>Someone read data worth recording the reading of.</summary>
    Access,

    /// <summary>A worker, scheduler or webhook acted with no originating user.</summary>
    System,

    /// <summary>Authorization denials and other security-relevant refusals.</summary>
    Security,
}

public enum AuditOutcome
{
    Success,

    /// <summary>Refused by authorization, as opposed to failing.</summary>
    Denied,

    Failure,
}

public enum AuditSeverity
{
    Info,
    Notice,
    Warning,
    Critical,
}

/// <summary>
/// How much the actor attribution can be trusted. A record that says "Alice deleted this"
/// means something different when Alice authenticated at this very request than when her
/// identity was asserted by a peer service across a message bus, and an investigator has to
/// be able to tell those apart.
/// </summary>
public enum AuditAttribution
{
    /// <summary>The actor authenticated on the request that produced this record.</summary>
    Direct,

    /// <summary>Carried from an originating request over NATS headers — a peer's assertion.</summary>
    Propagated,

    /// <summary>Derived from context with no authenticated principal, e.g. the HMAC git webhook.</summary>
    Inferred,
}

/// <summary>Who acted. See <see cref="ActorKind"/> for why this is not just a user id.</summary>
/// <param name="Id">User or visitor id. Null whenever the identity is not a Guid.</param>
/// <param name="Ref">Non-Guid identity: client_id, service name, plugin id, repo full name.</param>
/// <param name="Display">Email or display name, captured so the log survives account deletion.</param>
public sealed record AuditActor(
    ActorKind Kind,
    Guid? Id,
    string? Ref,
    string? Display,
    AuditAttribution Attribution)
{
    public static readonly AuditActor Anonymous =
        new(ActorKind.Anonymous, null, null, null, AuditAttribution.Direct);

    public static AuditActor ForService(string serviceName) =>
        new(ActorKind.System, null, serviceName, serviceName, AuditAttribution.Direct);
}

/// <summary>One field's before and after. Already redacted by the time it gets here.</summary>
/// <param name="Redacted">
/// True when the values were withheld rather than absent — the difference between
/// "this secret changed" and "this field did not change", which must stay legible.
/// </param>
public sealed record AuditFieldChange(string Field, object? Before, object? After, bool Redacted);

/// <summary>The HTTP shape of the originating request, when there was one.</summary>
/// <param name="IpTrusted">
/// False whenever the address came from a forwarding header the service accepts from any
/// proxy — which is the case for admin-api and identity today, since both call
/// UseForwardedHeaders with KnownProxies cleared. A false here means "the caller said so".
/// </param>
public sealed record AuditHttpInfo(
    string? Method,
    string? RoutePattern,
    int? StatusCode,
    string? IpAddress,
    bool IpTrusted,
    string? UserAgent);

/// <summary>
/// One audit record, in flight. Implements <see cref="IDcmsEvent"/> so it can ride the
/// existing JetStream path unchanged, but it lives here rather than in
/// Dcms.Shared.Contracts because it references <see cref="ActorKind"/> from the Kernel, and
/// both Contracts and Kernel are deliberately dependency-free.
///
/// Wide and mostly optional by design: one shape has to describe an HTTP mutation, a failed
/// login that belongs to no tenant, a git push with no user, and a worker's side effect on
/// an external system. Fields that do not apply stay null rather than being faked.
///
/// <see cref="Metadata"/> plus <see cref="SchemaVersion"/> are the forward-compatibility
/// escape hatch: a new kind of action carries new facts without a schema migration.
/// </summary>
public sealed record AuditEvent : IDcmsEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Producer clock, and <b>immutable</b>: it is the partition key and half of the dedup
    /// key <c>(OccurredAt, EventId)</c>, so a redelivered or retried copy must carry the
    /// identical value. Never re-stamp this at write time.
    /// </summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public int Version => 1;

    /// <summary>
    /// <see cref="Guid.Empty"/> means platform scope (a login, a tenant being provisioned) —
    /// deliberately not null. The RLS policy compares <c>"TenantId" = …::uuid</c>, and
    /// <c>NULL = x</c> is NULL rather than TRUE, so a null here would make the row invisible
    /// to every RLS-constrained reader rather than merely to other tenants.
    /// </summary>
    public Guid TenantId { get; init; }

    /// <summary>Dotted action key. See <see cref="AuditActions"/>.</summary>
    public required string Action { get; init; }

    public AuditCategory Category { get; init; } = AuditCategory.TenantState;
    public AuditOutcome Outcome { get; init; } = AuditOutcome.Success;
    public AuditSeverity Severity { get; init; } = AuditSeverity.Info;

    public required AuditActor Actor { get; init; }

    /// <summary>
    /// The user this event is *about*, when that differs from the actor — "an admin disabled
    /// user X", or a login, where the tenant read plane uses it to resolve which of its
    /// members a platform-scope auth event belongs to.
    /// </summary>
    public Guid? SubjectUserId { get; init; }

    public string? ResourceType { get; init; }

    /// <summary>Text, not a Guid: some resources are keyed by slug, hostname or file path.</summary>
    public string? ResourceId { get; init; }

    public string? ResourceLabel { get; init; }

    // ---- provenance ----
    /// <summary>Emitting service, as passed to AddDcmsServiceDefaults.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Process identity, minted at startup. With ProducerSeq, makes gaps detectable.</summary>
    public required string ServiceInstance { get; init; }

    /// <summary>
    /// Per-instance monotonic counter. A hash chain proves nothing about records that were
    /// never written; this is what makes an omission — a dropped buffer, a killed process —
    /// visible to a verifier.
    /// </summary>
    public long ProducerSeq { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>The audit event that caused this one — links a build back to the publish click.</summary>
    public Guid? CausationId { get; init; }

    public string? TraceId { get; init; }
    public string? SpanId { get; init; }

    public AuditHttpInfo? Http { get; init; }

    /// <summary>Preview traffic is flagged, never hidden — a sandbox action is still an action.</summary>
    public bool IsSandbox { get; init; }

    /// <summary>Free-form per-action facts: bulk counts, commit shas, permission keys, reasons.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();

    public IReadOnlyList<AuditFieldChange>? Changes { get; init; }

    /// <summary>Which redaction policy produced <see cref="Changes"/>, so later reads stay legible.</summary>
    public int RedactionVersion { get; init; }

    public int SchemaVersion { get; init; } = 1;
}
