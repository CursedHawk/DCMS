using Dcms.Shared.Audit.Http;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Builder;

namespace Dcms.Shared.Security;

/// <summary>
/// Endpoint metadata carrying the required permission key. The authorization
/// middleware resolves the tenant from the request and evaluates the caller's
/// effective permission set against this.
/// </summary>
/// <remarks>
/// Implements <see cref="IAuditPermission"/> so the audit middleware can record which
/// permission key gated an action — the first thing anyone reviewing a refusal wants to know.
/// The interface lives in the audit library so this stays a one-way dependency.
/// </remarks>
public sealed record PermissionMetadata(string Permission) : IAuditPermission;

/// <summary>
/// Marks an endpoint as deliberately ungated by a permission key.
///
/// <para>A handful of routes genuinely need this: anonymous delivery-plane reads, the auth
/// handshake itself, and endpoints whose only authority check is something other than a
/// permission (a signed token, a webhook signature, ownership of the row). It is a
/// <b>declaration</b>, not silence — the coverage test accepts this and rejects an endpoint
/// that simply forgot.</para>
/// </summary>
/// <param name="Reason">Why. Read by whoever revisits the exemption later.</param>
public sealed record PermissionExemptMetadata(string Reason);

public static class PermissionEndpointExtensions
{
    /// <summary>
    /// Requires an authenticated caller holding <paramref name="permission"/> in
    /// the current tenant (SuperAdmin bypasses). Wires both endpoint metadata and
    /// the dynamic permission policy.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new PermissionMetadata(permission));
        builder.RequireAuthorization(PermissionPolicyProvider.PolicyName(permission));
        return builder;
    }

    /// <summary>
    /// Declares that an endpoint is gated by something other than a permission key, and says
    /// what. Sits in the same fluent chain as <see cref="RequirePermission"/>.
    /// </summary>
    public static TBuilder PermissionExempt<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new PermissionExemptMetadata(reason));
        return builder;
    }
}
