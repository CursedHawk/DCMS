using Dcms.Shared.Data.Rls;

namespace Dcms.AdminApi.Tenancy.Usage;

/// <summary>
/// Where each tenant table's bytes are shown. Its keys must equal
/// <see cref="RlsConfigurator.TenantTables"/> (<c>TenantUsageMapTests</c>), so a new tenant table
/// fails the build until somebody decides what it is, rather than vanishing from the total.
/// </summary>
public static class TenantUsageMap
{
    /// <param name="Category">media, sites, plugins, dynamicApps, ai, audit or workspace.</param>
    /// <param name="Item">The item key, when the whole table is one item.</param>
    /// <param name="Group">
    /// SQL for the plugin id each row belongs to, over alias <c>x</c>; the item becomes
    /// <c>plugin:{id}</c>. Never null in its result.
    /// </param>
    /// <param name="Join">Joins <see cref="Group"/> needs.</param>
    public sealed record Table(string Category, string Item, string? Group = null, string? Join = null);

    private static Table Plugin(string pluginId) => new("plugins", $"plugin:{pluginId}");

    private static readonly Table Workspace = new("workspace", "members");

    // Rows whose instance was deleted have no plugin left to name.
    private const string ByInstance =
        """LEFT JOIN plugins.plugin_instances i ON i."Id" = x."PluginInstanceId" """;

    public static readonly IReadOnlyDictionary<(string Schema, string Table), Table> Tables =
        new Dictionary<(string, string), Table>
        {
            [("tenancy", "domains")] = new("workspace", "domains"),
            [("tenancy", "tenant_memberships")] = Workspace,
            [("tenancy", "tenant_roles")] = Workspace,
            [("tenancy", "tenant_role_permissions")] = Workspace,
            [("tenancy", "member_roles")] = Workspace,
            [("tenancy", "invitations")] = Workspace,
            [("tenancy", "tenant_usage")] = new("workspace", "settings"),
            [("tenancy", "table_views")] = new("workspace", "settings"),
            [("tenancy", "table_view_shares")] = new("workspace", "settings"),
            [("tenancy", "table_view_defaults")] = new("workspace", "settings"),
            [("plugins", "plugin_instances")] = new("plugins", "", Group: "x.\"PluginId\""),
            [("plugins", "plugin_data")] = new("plugins", "", Group: "x.\"PluginId\""),
            [("plugins", "plugin_secrets")] = new("plugins", "", Group: "x.\"PluginId\""),
            [("cms", "api_connections")] = new("workspace", "connections"),
            [("cms", "api_snapshots")] = new("workspace", "connections"),
            [("cms", "content_items")] = new("plugins", "",
                Group: "coalesce(i.\"PluginId\", 'content')", Join: ByInstance),
            [("cms", "content_versions")] = new("plugins", "",
                Group: "coalesce(i.\"PluginId\", 'content')",
                Join: """LEFT JOIN cms.content_items ci ON ci."Id" = x."ItemId" LEFT JOIN plugins.plugin_instances i ON i."Id" = ci."PluginInstanceId" """),
            [("media", "media_assets")] = new("media", "index"),
            [("media", "media_variants")] = new("media", "index"),
            [("media", "media_folders")] = new("media", "index"),
            [("sites", "sites")] = new("sites", "definitions"),
            [("sites", "site_builds")] = new("sites", "definitions"),
            [("sites", "site_drafts")] = new("sites", "definitions"),
            [("sites", "site_publish_branches")] = new("sites", "definitions"),
            [("ai", "tenant_ai_settings")] = new("ai", "assistant"),
            [("ai", "user_ai_settings")] = new("ai", "assistant"),
            [("ai", "conversations")] = new("ai", "assistant"),
            [("ai", "messages")] = new("ai", "assistant"),
            [("ai", "runs")] = new("ai", "assistant"),
            [("search", "search_documents")] = Plugin("search"),
            [("analytics", "events")] = Plugin("analytics"),
            [("analytics", "daily_rollups")] = Plugin("analytics"),
            [("visitors", "visitor_accounts")] = Plugin("visitor-auth"),
            [("visitors", "visitor_refresh_tokens")] = Plugin("visitor-auth"),
            [("chat", "conversations")] = Plugin("live-chat"),
            [("chat", "messages")] = Plugin("live-chat"),
            [("forms", "form_submissions")] = Plugin("forms"),
            // Instagram and Facebook share these tables.
            [("social", "meta_connections")] = new("plugins", "social"),
            [("social", "meta_sync_states")] = new("plugins", "social"),
            [("social", "meta_media_map")] = new("plugins", "social"),
            [("social", "meta_oauth_states")] = new("plugins", "social"),
            [("notifications", "notifications")] = new("workspace", "notifications"),
            [("notifications", "recipients")] = new("workspace", "notifications"),
            [("audit", "audit_events")] = new("audit", "auditLog"),
            [("apps", "apps")] = new("dynamicApps", "configuration"),
            [("apps", "revisions")] = new("dynamicApps", "configuration"),
            [("apps", "changes")] = new("dynamicApps", "configuration"),
            [("apps", "records")] = new("dynamicApps", "records"),
            [("apps", "relation_links")] = new("dynamicApps", "records"),
            [("apps", "unique_keys")] = new("dynamicApps", "records"),
            [("apps", "flow_runs")] = new("dynamicApps", "automation"),
            [("apps", "flow_run_steps")] = new("dynamicApps", "automation"),
            [("apps", "flow_schedules")] = new("dynamicApps", "automation"),
            [("apps", "site_views")] = new("dynamicApps", "views"),
            [("apps", "site_view_defaults")] = new("dynamicApps", "views"),
            [("userauth", "roles")] = Plugin("user-auth"),
            [("userauth", "grants")] = Plugin("user-auth"),
            [("userauth", "gates")] = Plugin("user-auth"),
            [("userauth", "api_rules")] = Plugin("user-auth"),
        };

    /// <summary>English labels for the fixed items. The SPAs translate by key and fall back to these.</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["domains"] = "Domains",
        ["members"] = "Members, roles and invitations",
        ["settings"] = "Usage snapshot",
        ["connections"] = "API connections",
        ["notifications"] = "Notifications",
        ["index"] = "Media library records",
        ["originals"] = "Original files",
        ["renditions"] = "Optimized copies",
        ["definitions"] = "Site definitions and build history",
        ["assistant"] = "AI assistant conversations",
        ["social"] = "Instagram and Facebook",
        ["auditLog"] = "Audit log",
        ["configuration"] = "App configuration and history",
        ["records"] = "Records",
        ["automation"] = "Automation runs",
        ["views"] = "Saved views on your sites",
        ["plugin:content"] = "Content of deleted plugins",
    };
}
