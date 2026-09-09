namespace Dcms.Shared.Security;

/// <summary>
/// Platform-level permission keys. Plugin permissions follow the convention
/// plugin:{pluginId}:{action} and are contributed by plugin manifests; the
/// effective catalog per tenant is the union of these and enabled plugins'.
/// </summary>
public static class PlatformPermissions
{
    public const string TenantSettings = "tenant:settings";
    public const string MembersManage = "members:manage";
    public const string RolesManage = "roles:manage";
    public const string DomainsManage = "domains:manage";
    public const string PluginsManage = "plugins:manage";
    public const string MediaRead = "media:read";
    public const string MediaWrite = "media:write";
    public const string SiteEdit = "site:edit";
    public const string SitePublish = "site:publish";
    public const string AiSettings = "ai:settings";

    /// <summary>
    /// Read every member's assistant conversation, not only your own and the shared ones.
    ///
    /// <para>A scope escalation rather than a feature: the assistant writes to tenant content,
    /// so its transcripts are the record of who asked for what. Held by the roles that already
    /// answer for the workspace.</para>
    /// </summary>
    public const string AiChatsReadAll = "ai:chats:read-all";
    public const string AnalyticsRead = "analytics:read";
    public const string ContentRead = "content:read";
    public const string ContentWrite = "content:write";
    public const string ContentPublish = "content:publish";
    public const string ChatRead = "chat:read";
    public const string ChatManage = "chat:manage";
    public const string AuditRead = "audit:read";
    public const string AuditExport = "audit:export";

    /// <summary>All platform permission keys (excludes plugin-contributed ones).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        TenantSettings, MembersManage, RolesManage, DomainsManage, PluginsManage,
        MediaRead, MediaWrite, SiteEdit, SitePublish, AiSettings, AiChatsReadAll, AnalyticsRead,
        ContentRead, ContentWrite, ContentPublish, ChatRead, ChatManage, AuditRead, AuditExport,
    ];

    public static string ForPlugin(string pluginId, string action) => $"plugin:{pluginId}:{action}";

    // ---- Per-site git repository permissions (resource-scoped) ----
    // Each Mode B site has its own repo; access is granted per-site so a role can
    // permit pull (read) and/or push (write) on individual repos. Keys are
    // "repo:{siteId}:read" / "repo:{siteId}:write" and are stored like any other
    // permission string; write implies read.

    public const string RepoPrefix = "repo:";

    public static string RepoRead(Guid siteId) => $"repo:{siteId}:read";
    public static string RepoWrite(Guid siteId) => $"repo:{siteId}:write";

    /// <summary>Parse a per-site repo permission key. Returns false for non-repo keys.</summary>
    public static bool TryParseRepo(string key, out Guid siteId, out bool write)
    {
        siteId = default;
        write = false;
        if (!key.StartsWith(RepoPrefix, StringComparison.Ordinal)) return false;
        var rest = key.AsSpan(RepoPrefix.Length);
        var sep = rest.LastIndexOf(':');
        if (sep <= 0) return false;
        var level = rest[(sep + 1)..];
        if (level.SequenceEqual("write")) write = true;
        else if (!level.SequenceEqual("read")) return false;
        return Guid.TryParse(rest[..sep], out siteId);
    }
}
