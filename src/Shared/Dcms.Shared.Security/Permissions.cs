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
    public const string AnalyticsRead = "analytics:read";
    public const string ContentRead = "content:read";
    public const string ContentWrite = "content:write";
    public const string ContentPublish = "content:publish";
    public const string ChatRead = "chat:read";
    public const string ChatManage = "chat:manage";

    /// <summary>All platform permission keys (excludes plugin-contributed ones).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        TenantSettings, MembersManage, RolesManage, DomainsManage, PluginsManage,
        MediaRead, MediaWrite, SiteEdit, SitePublish, AiSettings, AnalyticsRead,
        ContentRead, ContentWrite, ContentPublish, ChatRead, ChatManage,
    ];

    public static string ForPlugin(string pluginId, string action) => $"plugin:{pluginId}:{action}";
}
