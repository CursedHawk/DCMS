using System.Globalization;
using Dcms.Shared.Kernel.Abstractions;

namespace Dcms.Shared.Audit.Propagation;

/// <summary>
/// Carries who-and-why across a process boundary.
///
/// <para>An action on this platform rarely ends in the request that started it. Clicking
/// publish writes a row, a dispatcher picks it up two seconds later, JetStream hands it to the
/// site builder, the builder writes artifacts and publishes again, and a cache invalidator
/// hears about that. Five records, one human — and without this, four of them say the actor
/// was a background service, which is true and useless.</para>
///
/// <para>Deliberately a flat set of string headers rather than a field on
/// <see cref="Contracts.Events.IDcmsEvent"/>: the fifteen event records stay unchanged, and a
/// consumer that knows nothing about audit simply ignores headers it does not read. The same
/// shape serves NATS headers and the <c>ContextJson</c> column on the two database outboxes,
/// because a two-second poll loses the request context just as thoroughly as a message bus
/// does.</para>
///
/// <para><b>These values are an assertion by a peer, not an authentication.</b> Anything
/// restored from them is stamped <see cref="AuditAttribution.Propagated"/>, so a record saying
/// "Alice published this site" can still be told apart from one written by the request Alice
/// actually authenticated on.</para>
/// </summary>
public static class AuditPropagation
{
    public const string CorrelationId = "Dcms-Correlation-Id";
    public const string CausationId = "Dcms-Causation-Id";
    public const string TraceId = "Dcms-Trace-Id";

    /// <summary>
    /// W3C trace context, so the consumer's span becomes a <i>child</i> of the producer's rather
    /// than a second root that merely shares a trace id.
    ///
    /// <para><see cref="TraceId"/> alone was not enough and could not be made enough. It carries
    /// no span id, so a consumer restoring it knows which trace it belongs to but not what it
    /// hangs off; every hop across the bus therefore started a fresh root, and the publish that
    /// a person clicked and the build that resulted appeared in the trace store as unrelated
    /// trees. This header carries the parent span id and the sampling decision as well, which is
    /// what makes <c>request → outbox → JetStream → builder</c> one trace.</para>
    ///
    /// <para><see cref="TraceId"/> is kept, unchanged and still populated. A message already
    /// sitting in a stream from before this header existed restores exactly as it did, and the
    /// audit record's <c>TraceId</c> column does not depend on span plumbing being present.</para>
    /// </summary>
    public const string TraceParent = "traceparent";

    /// <summary>Vendor-specific trace state, carried verbatim when present.</summary>
    public const string TraceState = "tracestate";
    public const string Tenant = "Dcms-Tenant";
    public const string Sandbox = "Dcms-Sandbox";
    public const string ActorKind = "Dcms-Actor-Kind";
    public const string ActorId = "Dcms-Actor-Id";
    public const string ActorRef = "Dcms-Actor-Ref";
    public const string ActorDisplay = "Dcms-Actor-Display";

    /// <summary>
    /// Flattens the ambient context into headers. Empty when there is nothing to say, so a
    /// publish from a process with no originating request adds no headers at all rather than a
    /// set of blanks that later read as a deliberate "unknown".
    /// </summary>
    public static Dictionary<string, string> Capture(AuditScope? scope)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (scope is null)
        {
            return headers;
        }

        Set(headers, CorrelationId, scope.CorrelationId);
        Set(headers, TraceId, scope.TraceId);

        // A live span wins over a stored one: the scope's TraceId was captured when the unit of
        // work opened, while the span actually publishing may be a descendant of it, and a
        // consumer that republishes should parent the new message to its own span rather than to
        // the one two hops back.
        //
        // The fallback is what makes the database outboxes work. The dispatcher publishes on a
        // two-second timer with no current activity at all, minutes after the request that
        // enqueued the row finished — so without the stored value the trace would break exactly
        // where the audit design already goes to some trouble to keep the actor intact, and
        // "the site build attributes back to the human who clicked publish" would hold for the
        // actor while the trace showed two unrelated trees.
        if (System.Diagnostics.Activity.Current is { } activity)
        {
            Set(headers, TraceParent, activity.Id);
            Set(headers, TraceState, activity.TraceStateString);
        }
        else
        {
            Set(headers, TraceParent, scope.TraceParent);
            Set(headers, TraceState, scope.TraceState);
        }

        // The record that caused whatever this message will become. The endpoint's own entry is
        // the right answer: it is the thing a person did, and everything downstream is a
        // consequence of it.
        var causation = scope.Declared?.EventId ?? scope.CausationId;
        if (causation is { } id && id != Guid.Empty)
        {
            headers[CausationId] = id.ToString();
        }

        if (scope.TenantId is { } tenant && tenant != Guid.Empty)
        {
            headers[Tenant] = tenant.ToString();
        }

        if (scope.IsSandbox)
        {
            headers[Sandbox] = "1";
        }

        var actor = scope.ResolveActor();
        if (actor.Kind != Kernel.Abstractions.ActorKind.Anonymous)
        {
            headers[ActorKind] = actor.Kind.ToString();
            Set(headers, ActorId, actor.Id?.ToString());
            Set(headers, ActorRef, actor.Ref);
            Set(headers, ActorDisplay, actor.Display);
        }

        return headers;
    }

    /// <summary>
    /// Rebuilds a scope from whatever a carrier can supply.
    ///
    /// <para>Takes a lookup rather than a dictionary so one implementation serves NATS headers,
    /// a JSON column and a test, and so a missing header is indistinguishable from a carrier
    /// that has none — which is the correct reading either way.</para>
    /// </summary>
    /// <param name="tenantFallback">
    /// The tenant from the message payload, used when no header carried one. Workers have no
    /// ambient tenant, and a record that landed on the platform scope because a header was
    /// dropped would be invisible to the tenant it belongs to.
    /// </param>
    public static void Restore(AuditScope scope, Func<string, string?> header, Guid? tenantFallback = null)
    {
        scope.CorrelationId = header(CorrelationId) ?? scope.CorrelationId;
        scope.TraceId = header(TraceId) ?? scope.TraceId;
        scope.TraceParent = header(TraceParent) ?? scope.TraceParent;
        scope.TraceState = header(TraceState) ?? scope.TraceState;

        if (Guid.TryParse(header(CausationId), out var causation))
        {
            scope.CausationId = causation;
        }

        scope.TenantId = Guid.TryParse(header(Tenant), out var tenant) && tenant != Guid.Empty
            ? tenant
            : tenantFallback ?? scope.TenantId;

        scope.IsSandbox = header(Sandbox) == "1" || scope.IsSandbox;

        if (header(ActorKind) is { Length: > 0 } kindText
            && Enum.TryParse<ActorKind>(kindText, ignoreCase: true, out var kind))
        {
            scope.Actor = new AuditActor(
                kind,
                Guid.TryParse(header(ActorId), out var id) ? id : null,
                header(ActorRef),
                header(ActorDisplay),
                // The distinction the whole mechanism exists to preserve: this identity crossed
                // a boundary on someone else's word.
                AuditAttribution.Propagated);
        }
    }

    /// <summary>
    /// Serialises the captured headers for storage in a database outbox row, where they wait
    /// out the gap between the transaction that enqueued the work and the poll that publishes it.
    /// </summary>
    public static string? ToJson(Dictionary<string, string> headers) =>
        headers.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(headers);

    /// <summary>What an outbox row stores: <see cref="Capture"/> flattened to JSON, or null.</summary>
    public static string? CaptureJson(AuditScope? scope) => ToJson(Capture(scope));

    public static Func<string, string?> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return static _ => null;
        }

        Dictionary<string, string>? values;
        try
        {
            values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            // A row whose context will not parse still has work to do. Losing the attribution
            // is bad; refusing to publish the event because of it is worse.
            return static _ => null;
        }

        return values is null
            ? static _ => null
            : key => values.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// The producer's span, if the carrier had one, for a consumer to start a child of.
    ///
    /// <para>Returns <c>default</c> — which <c>ActivitySource.StartActivity</c> reads as "no
    /// parent, start a root" — when the header is absent or malformed. That is the right answer
    /// for a message published before this header existed, and for one whose headers were
    /// dropped: an unparented span is a worse trace, not a failure.</para>
    /// </summary>
    public static System.Diagnostics.ActivityContext ParentContext(Func<string, string?> header)
    {
        var traceParent = header(TraceParent);
        if (string.IsNullOrEmpty(traceParent))
        {
            return default;
        }

        return System.Diagnostics.ActivityContext.TryParse(traceParent, header(TraceState), out var context)
            ? context
            : default;
    }

    private static void Set(Dictionary<string, string> headers, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            headers[key] = value;
        }
    }

    /// <summary>Invariant string form, for the numeric headers a future version may add.</summary>
    internal static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
