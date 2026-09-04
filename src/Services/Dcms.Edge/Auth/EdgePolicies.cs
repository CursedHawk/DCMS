namespace Dcms.Edge.Auth;

/// <summary>
/// The authorization policies routes name, and what each one means.
///
/// <para>Deliberately role-shaped rather than permission-shaped. The permission keys in
/// <c>PlatformConsolePermissions</c> are resolved from <c>platform.roles</c> by
/// <c>IPlatformPermissionResolver</c>, which reads platform-api's schema — a grant the public
/// ingress should not hold, and a database round trip on every request to a gated host. The
/// role claim is in the token the edge already validated.</para>
/// </summary>
public static class EdgePolicies
{
    /// <summary>
    /// Signed in as anybody DCMS knows. Used for the Forgejo web UI, whose accounts mirror
    /// every DCMS user and which does its own per-repository authorization once it knows who
    /// is asking.
    /// </summary>
    public const string SignedIn = "edge.signed-in";

    /// <summary>
    /// Platform SuperAdmin. Used for Grafana, where the alternative is not "less access" but
    /// "all of it": these dashboards carry every tenant's usage, every audit action and every
    /// trace on the platform.
    ///
    /// <para>The same rule the Grafana OIDC mapping enforced before this
    /// (<c>contains(roles[*], 'SuperAdmin')</c> with <c>role_attribute_strict</c>), moved to the
    /// edge so an unauthorized request is refused before it reaches Grafana at all.</para>
    /// </summary>
    public const string SuperAdmin = "edge.superadmin";

    /// <summary>
    /// The role name, and the claim type it arrives under.
    ///
    /// <para>Both are literals here on purpose, matching <c>PlatformPermissionAuthorizationHandler</c>
    /// and <c>CurrentActors.IsSuperAdmin</c>, which spell them the same way. A shared constant
    /// would be tidier and would also make one edit change who can read every tenant's data in
    /// three services at once.</para>
    /// </summary>
    public const string SuperAdminRole = "SuperAdmin";

    public const string RoleClaimType = "role";
}
