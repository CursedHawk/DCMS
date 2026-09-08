using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;

namespace Dcms.AdminApi.Tenancy;

/// <summary>
/// Restores the human behind a service-to-service call, so the audit log names them.
///
/// <para><b>The failure this exists to prevent.</b> A client-credentials token's <c>sub</c> is
/// a client id, not a user id, so <c>HttpCurrentActor</c> reports
/// <see cref="Dcms.Shared.Kernel.Abstractions.ActorKind.ServiceClient"/>. When the platform
/// console's API calls admin-api to suspend a tenant on an operator's behalf, the record left
/// behind therefore reads "ServiceClient:dcms-platform-api-service suspended tenant X" — which
/// is true of the last hop and useless about the decision. The console's own audit page would
/// then show a service acting on the platform and no operator anywhere.</para>
///
/// <para>The mechanism already existed for the bus: every NATS consumer calls
/// <see cref="AuditPropagation.Restore"/> so a site build attributes back to whoever clicked
/// publish. It was never wired into HTTP because until now nothing called admin-api on a
/// person's behalf. This is that wiring, and nothing more.</para>
///
/// <para><b>Only where a service was already invited.</b> Runs after
/// <see cref="ServicePrincipalGuard"/>, so by the time it sees a request the caller has been
/// checked against the endpoint's <see cref="AllowServicePrincipalAttribute"/>. A request with
/// a user behind it is left entirely alone: propagation headers on such a request are somebody
/// else's assertion about a caller we can see for ourselves, and would let any authenticated
/// user rewrite their own audit trail.</para>
///
/// <para><b>These headers are an assertion, not an authentication</b>, which is why the actor
/// is stamped <see cref="AuditAttribution.Propagated"/> — the record says Alice suspended the
/// tenant and says honestly that it heard so from a peer. Authorization is unaffected: the
/// caller is still the service principal, and the operator's permission was decided by the
/// service that captured these headers.</para>
/// </summary>
public sealed class PropagatedActorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUser me, AuditScope scope)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && me.UserId is null
            && context.GetEndpoint()?.Metadata.GetMetadata<AllowServicePrincipalAttribute>() is not null)
        {
            // Tenant deliberately left to the request's own resolution: X-Dcms-Tenant and the
            // route already decided it, and a propagated tenant header could move a record onto
            // a scope the caller never asked for.
            var tenantBefore = scope.TenantId;
            AuditPropagation.Restore(scope, key => context.Request.Headers.TryGetValue(key, out var v)
                ? v.ToString()
                : null);
            scope.TenantId = tenantBefore;
        }

        await next(context);
    }
}

public static class PropagatedActorMiddlewareExtensions
{
    /// <summary>
    /// Restores propagated attribution on service-to-service calls. Register immediately after
    /// <c>UseServicePrincipalGuard</c>: before it, the guard has not yet decided whether this
    /// caller was invited at all.
    /// </summary>
    public static IApplicationBuilder UsePropagatedActor(this IApplicationBuilder app) =>
        app.UseMiddleware<PropagatedActorMiddleware>();
}
