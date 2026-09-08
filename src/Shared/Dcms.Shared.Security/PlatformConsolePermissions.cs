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

    /// <summary>
    /// Catalog-only, and deliberately: the user directory is served by identity, which gates it
    /// on the SuperAdmin <i>role</i> rather than on a key in this table. Moving it here would
    /// close an escalation loop — <see cref="RolesManage"/> edits this table, so whoever held it
    /// could grant themselves <see cref="UsersRoles"/> and then mint a SuperAdmin.
    /// </summary>
    public const string UsersRead = "platform:users:read";

    /// <inheritdoc cref="UsersRead"/>
    public const string UsersWrite = "platform:users:write";

    /// <summary>Grant/revoke a global role. Separated from UsersWrite because this one key is
    /// the difference between "may unlock an account" and "may create another SuperAdmin".
    /// Catalog-only for the reason on <see cref="UsersRead"/>, and this key is why.</summary>
    public const string UsersRoles = "platform:users:roles";

    public const string TenantsRead = "platform:tenants:read";

    /// <summary>
    /// Catalog-only: nothing on this console edits a tenant's own settings. Renaming a workspace
    /// or changing its plan happens inside it, on the admin plane, where the tenant's own
    /// permission model applies. The key is kept because the console is the obvious place for
    /// that to arrive, and a role seeded without it should not silently gain it later.
    /// </summary>
    public const string TenantsWrite = "platform:tenants:write";
    /// <summary>Suspend, resume and delete. Deletion purges every schema, the object store
    /// prefixes and the git org; it is not an ordinary write.</summary>
    public const string TenantsLifecycle = "platform:tenants:lifecycle";

    public const string AuditRead = "platform:audit:read";

    /// <summary>
    /// Catalog-only, and it is not clear it can be otherwise: traces are read in Grafana, which
    /// the edge gates by host and signs in by header. An operator either reaches Grafana or does
    /// not, and this key cannot narrow that from here.
    /// </summary>
    public const string TracesRead = "platform:traces:read";

    /// <inheritdoc cref="TracesRead"/>
    public const string ObservabilityRead = "platform:observability:read";

    /// <summary>
    /// Catalog-only: the console's read-only operational pages — stores, capacity, queue depth —
    /// are served under <see cref="OverviewRead"/>, which every role that can open the console
    /// already holds. Splitting them out would be a distinction with no page behind it.
    /// </summary>
    public const string OpsRead = "platform:ops:read";

    /// <summary>Retention deletes and anything else that changes platform state from an
    /// operations page. Enforced on the analytics prune.</summary>
    public const string OpsAct = "platform:ops:act";

    public const string LogsRead = "platform:logs:read";
    /// <summary>Irreversible deletion from a telemetry store. Never reaches audit rows.</summary>
    public const string LogsPurge = "platform:logs:purge";

    public const string RolesManage = "platform:roles:manage";

    /// <summary>
    /// The console's own bell — certificate failures, expiries and the other platform-wide
    /// facts nothing else surfaces.
    ///
    /// <para>A ":read" key with no ":write" sibling, and deliberately so: the only writes are
    /// read and dismiss, which are one operator's own UI state and are not a permission
    /// question. Being a ":read" key it joins <see cref="ReadOnly"/>, which is right — a
    /// support operator who can see that a certificate failed is the point of the bell.</para>
    /// </summary>
    public const string NotificationsRead = "platform:notifications:read";

    /// <summary>
    /// Which domains the platform holds TLS certificates for, and asking for one to be reissued.
    ///
    /// <para>Deliberately a single ":manage" key with no ":read" sibling, unlike most areas here. The
    /// endpoints behind it are SuperAdmin-only — they decide which names this platform serves
    /// TLS for, which is not a support question — and a ":read" key would be swept into
    /// <see cref="ReadOnly"/> by the derivation below, showing the support role a page whose
    /// every request it would then be refused.</para>
    /// </summary>
    public const string CertificatesManage = "platform:certificates:manage";

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
        CertificatesManage,
        NotificationsRead,
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
