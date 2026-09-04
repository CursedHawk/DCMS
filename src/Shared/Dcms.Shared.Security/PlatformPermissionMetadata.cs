using Dcms.Shared.Audit.Http;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Builder;

namespace Dcms.Shared.Security;

/// <summary>
/// Endpoint metadata carrying the required platform-console permission key.
/// </summary>
/// <remarks>
/// Implements <see cref="IAuditPermission"/> for the same reason
/// <see cref="PermissionMetadata"/> does: a refusal is only useful in the audit log if it
/// records which key gated it.
/// </remarks>
public sealed record PlatformPermissionMetadata(string Permission) : IAuditPermission;

public static class PlatformPermissionEndpointExtensions
{
    /// <summary>
    /// Requires an authenticated caller whose global roles carry
    /// <paramref name="permission"/> (SuperAdmin bypasses). There is no tenant in this
    /// evaluation — see <see cref="PlatformConsolePermissions"/>.
    /// </summary>
    public static TBuilder RequirePlatformPermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new PlatformPermissionMetadata(permission));
        builder.RequireAuthorization(PermissionPolicyProvider.PlatformPolicyName(permission));
        return builder;
    }
}
