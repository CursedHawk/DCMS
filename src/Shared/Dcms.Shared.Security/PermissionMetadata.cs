using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Builder;

namespace Dcms.Shared.Security;

/// <summary>
/// Endpoint metadata carrying the required permission key. The authorization
/// middleware resolves the tenant from the request and evaluates the caller's
/// effective permission set against this.
/// </summary>
public sealed record PermissionMetadata(string Permission);

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
