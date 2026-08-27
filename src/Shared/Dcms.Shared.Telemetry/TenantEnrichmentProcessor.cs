using System.Diagnostics;
using Dcms.Shared.Audit.Propagation;
using OpenTelemetry;

namespace Dcms.Shared.Telemetry;

/// <summary>
/// Puts the tenant, the actor and the correlation id onto every span, so a trace can be
/// filtered to one tenant without the caller of every instrumented method having to remember
/// to pass them.
///
/// <para>These facts already exist, once per unit of work, in the <see cref="AuditScope"/> that
/// the audit middleware or a message consumer established. They are read from
/// <see cref="AuditAmbient"/> rather than from scoped dependency injection because a span
/// processor is a singleton — resolving a scoped service from one would either throw or, worse,
/// capture the first request's scope and stamp it onto everything afterwards.</para>
///
/// <para>Stamped in <c>OnEnd</c>, not <c>OnStart</c>: the audit middleware runs before
/// authentication, so at span-start the actor is not yet known and the tenant may not be
/// resolved. By the time the span ends both are settled.</para>
///
/// <para>Deliberately not stamped: the actor's id, reference or display name. The actor
/// <i>kind</i> is bounded and useful for filtering; the identity itself belongs in the audit
/// record, which is access-controlled, and not in a span store that a Grafana viewer can
/// query freely.</para>
/// </summary>
public sealed class TenantEnrichmentProcessor(AuditAmbient ambient) : BaseProcessor<Activity>
{
    public const string TenantIdTag = "dcms.tenant.id";
    public const string ActorKindTag = "dcms.actor.kind";
    public const string CorrelationIdTag = "dcms.correlation_id";
    public const string SandboxTag = "dcms.sandbox";

    public override void OnEnd(Activity activity)
    {
        var scope = ambient.Current;
        if (scope is null)
        {
            return;
        }

        if (scope.TenantId is { } tenantId && tenantId != Guid.Empty)
        {
            activity.SetTag(TenantIdTag, tenantId.ToString());
        }

        if (!string.IsNullOrEmpty(scope.CorrelationId))
        {
            activity.SetTag(CorrelationIdTag, scope.CorrelationId);
        }

        // ResolveActor() runs the middleware's lazy resolver, which reads the principal. Safe
        // here and not at start: authentication has run by the time any span ends.
        activity.SetTag(ActorKindTag, scope.ResolveActor().Kind.ToString());

        if (scope.IsSandbox)
        {
            activity.SetTag(SandboxTag, true);
        }

        base.OnEnd(activity);
    }
}
