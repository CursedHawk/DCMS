namespace Dcms.Shared.Audit;

/// <summary>
/// The ambient envelope for everything recorded during one unit of work — one HTTP request,
/// or one consumed message: who is acting, for which tenant, and how to correlate it.
/// Populated once by the audit middleware or by a consumer restoring propagated headers,
/// then merged into every <see cref="AuditEntry"/> at flush time.
///
/// Mutable, because several of these facts are not known when the first entry is recorded —
/// the status code in particular is settled only once the response has been produced.
/// </summary>
public sealed class AuditScope
{
    public string? CorrelationId { get; set; }
    public string? TraceId { get; set; }
    public string? SpanId { get; set; }

    /// <summary>The audit event that caused this whole scope, from propagated NATS headers.</summary>
    public Guid? CausationId { get; set; }

    /// <summary>
    /// The acting tenant. Consumers set this from the message payload, since workers have no
    /// Finbuckle tenant context; on HTTP paths the recorder falls back to
    /// <c>ITenantContext</c> when this is null.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>Preview traffic. Recorded, never dropped — a sandbox action is still an action.</summary>
    public bool IsSandbox { get; set; }

    /// <summary>
    /// Explicit actor for a unit of work whose caller cannot be read from claims: the
    /// HMAC-verified git webhook, a worker, or a consumer replaying a user's action. Null
    /// means "resolve from <c>ICurrentActor</c>".
    /// </summary>
    public AuditActor? Actor { get; set; }

    /// <summary>
    /// Where the actor comes from when nothing has overridden it. Set once per unit of work by
    /// whatever established the scope, and called late rather than eagerly: on an HTTP request
    /// this scope is opened <i>before</i> authentication runs, so reading the principal at that
    /// moment would record every caller as anonymous.
    /// </summary>
    public Func<AuditActor>? ActorResolver { get; set; }

    /// <summary>
    /// Who is acting: an explicit override, else the resolver, else nobody. Used both by the
    /// recorder when completing an entry and by the publisher when stamping propagation
    /// headers, so the actor a downstream service sees is the actor the record names.
    /// </summary>
    public AuditActor ResolveActor() => Actor ?? ActorResolver?.Invoke() ?? AuditActor.Anonymous;

    public AuditHttpInfo? Http { get; set; }

    /// <summary>The permission key that gated the endpoint, read from PermissionMetadata.</summary>
    public string? Permission { get; set; }

    /// <summary>
    /// True once anything has been recorded explicitly in this scope. Drives the middleware's
    /// generic fallback record: a mutating request that recorded nothing gets a coarse entry
    /// rather than silence.
    /// </summary>
    public bool HasExplicitRecord { get; set; }

    /// <summary>
    /// Suppresses only the generic fallback, for paths that are noise by definition — health
    /// checks, static assets. It never suppresses an explicitly recorded entry: silencing
    /// something a developer deliberately recorded would defeat the point.
    /// </summary>
    public bool SuppressFallback { get; set; }

    /// <summary>
    /// The entry opened by <c>.WithAudit(...)</c> before the handler ran. Held here so the
    /// EF layer can hang a field diff on the endpoint's own semantic record instead of
    /// emitting an anonymous second one, and so the middleware can withdraw it again if the
    /// handler turned out to change nothing.
    /// </summary>
    public AuditEntry? Declared { get; set; }

    /// <summary>
    /// While positive, set-based statements (<c>ExecuteUpdate</c>, <c>ExecuteDelete</c>) are not
    /// recorded one by one. A tenant purge runs twenty-five of them and deserves one record with
    /// a count manifest, not twenty-five rows that each say a table got shorter. Nested, so the
    /// block that raises it does not have to know whether an outer block already did.
    ///
    /// <para>Deliberately narrow: it does not touch the change tracker's diffs, which fold into
    /// whichever entry the caller opened and so cannot duplicate anything.</para>
    /// </summary>
    public int BulkCaptureSuppressions { get; set; }

    /// <summary>
    /// Stops per-statement recording of set-based work until disposed. The caller takes on the
    /// obligation to record what it destroyed, which is the trade: one meaningful record
    /// instead of many meaningless ones.
    /// </summary>
    public IDisposable SuppressBulkCapture()
    {
        BulkCaptureSuppressions++;
        return new Suppression(this);
    }

    private sealed class Suppression(AuditScope scope) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            scope.BulkCaptureSuppressions--;
        }
    }
}
