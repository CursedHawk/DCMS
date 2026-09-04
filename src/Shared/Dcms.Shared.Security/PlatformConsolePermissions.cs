namespace Dcms.Shared.Security;

/// <summary>
/// Permission keys for the platform console (platform.highgeek.eu) — the cross-tenant
/// operator surface, as distinct from <see cref="PlatformPermissions"/>, whose keys are
/// evaluated <i>inside</i> one tenant despite the name.
///
/// <para><b>Why the names look alike and are not.</b> <c>PlatformPermissions</c> predates the
/// console and is stored, as strings, in <c>tenancy.tenant_role_permissions</c> for every
/// tenant on the platform. Renaming the class would touch every call site in admin-api,
/// content-api and the plugins to buy nothing the persisted values could follow. So the older
/// name stays and the newer surface takes a prefix instead: every key here begins
/// <c>platform:</c>, which no tenant key, plugin key (<c>plugin:</c>) or repo key
/// (<c>repo:</c>) does. A key can therefore never be mistaken for the other kind by a string
/// comparison, which is the only comparison either system actually performs.</para>
///
/// <para><b>Scope.</b> These are held by a <i>global</i> role (identity's AspNetRoles —
/// SuperAdmin, Support), not by a tenant role, and they are never tenant-scoped. Holding
/// <c>platform:tenants:read</c> means "may read the tenant list of the whole platform"; it
/// grants nothing inside any individual tenant.</para>
/// </summary>
public static class PlatformConsolePermissions
{
    public const string OverviewRead = "platform:overview:read";

    public const string UsersRead = "platform:users:read";
    public const string UsersWrite = "platform:users:write";
    /// <summary>Grant/revoke a global role. Separated from UsersWrite because this one key is
    /// the difference between "may unlock an account" and "may create another SuperAdmin".</summary>
    public const string UsersRoles = "platform:users:roles";

    public const string TenantsRead = "platform:tenants:read";
    public const string TenantsWrite = "platform:tenants:write";
    /// <summary>Suspend, resume and delete. Deletion purges every schema, the object store
    /// prefixes and the git org; it is not an ordinary write.</summary>
    public const string TenantsLifecycle = "platform:tenants:lifecycle";

    public const string AuditRead = "platform:audit:read";
    public const string TracesRead = "platform:traces:read";
    public const string ObservabilityRead = "platform:observability:read";

    public const string OpsRead = "platform:ops:read";
    public const string OpsAct = "platform:ops:act";

    public const string LogsRead = "platform:logs:read";
    /// <summary>Irreversible deletion from a telemetry store. Never reaches audit rows.</summary>
    public const string LogsPurge = "platform:logs:purge";

    public const string RolesManage = "platform:roles:manage";

    /// <summary>The prefix every key here carries. Used to tell the two permission
    /// spaces apart when both could appear in one list.</summary>
    public const string Prefix = "platform:";

    public static readonly IReadOnlyList<string> All =
    [
        OverviewRead,
        UsersRead, UsersWrite, UsersRoles,
        TenantsRead, TenantsWrite, TenantsLifecycle,
        AuditRead, TracesRead, ObservabilityRead,
        OpsRead, OpsAct,
        LogsRead, LogsPurge,
        RolesManage,
    ];

    /// <summary>
    /// The read-only subset — everything that cannot change platform state. This is what a
    /// support role is seeded with, and it is derived from the key rather than listed a second
    /// time so a new read key cannot be forgotten here.
    /// </summary>
    public static readonly IReadOnlyList<string> ReadOnly =
        [.. All.Where(k => k.EndsWith(":read", StringComparison.Ordinal))];

    public static bool IsPlatformKey(string key) =>
        key.StartsWith(Prefix, StringComparison.Ordinal);
}
