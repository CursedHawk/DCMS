using Microsoft.AspNetCore.Authorization;

namespace Dcms.Shared.Security.Authorization;

public sealed class PlatformPermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Loads the effective platform-console permission set for a caller.
///
/// <para>Deliberately keyed on the caller's <i>global roles</i> rather than on a user id: a
/// platform permission is held by a role (identity's AspNetRoles — SuperAdmin, Support) and a
/// user holds it by holding that role. The access token already carries one <c>role</c> claim
/// per global role, so the resolver needs no user lookup at all — which is what lets
/// platform-api evaluate authorization without any grant on the identity schema.</para>
/// </summary>
public interface IPlatformPermissionResolver
{
    Task<IReadOnlySet<string>> GetPermissionsAsync(
        IReadOnlyCollection<string> roles, CancellationToken ct = default);
}
