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
}
