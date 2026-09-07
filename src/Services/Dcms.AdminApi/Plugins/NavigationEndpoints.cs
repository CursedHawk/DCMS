using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Dcms.AdminApi.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Plugins;

/// <summary>
/// The admin menu, for this caller in this tenant.
///
/// <para><b>Why the server draws it.</b> The SPA held a hard-coded array of sixteen destinations
/// filtered by permission, which was fine while every destination was part of the product. It
/// cannot answer the question a plugin system creates: a tenant with two Image Gallery instances
/// and no Forms instance should see two gallery entries and no forms entry, and nothing shipped
/// in a bundle knows either fact.</para>
///
/// <para>The platform sections are still declared in code — they are the product, and a database
/// round trip to discover that Media exists would be silly — but they are declared <i>here</i>,
/// next to the permission that gates each one, so the menu and the route guard are read from the
/// same list rather than two lists that have to be kept in step.</para>
///
/// <para>Icons travel as lucide names rather than markup. A console that does not recognise one
/// falls back to a generic glyph, which is the right failure for a client that may be older than
/// the server.</para>
/// </summary>
public static class NavigationEndpoints
{
    /// <summary>
    /// The product's own destinations: route, i18n key, icon, and the permission that opens it.
    /// A null permission means any signed-in member.
    /// </summary>
    public static readonly NavEntry[] Platform =
    [
        new("/", "nav.dashboard", "LayoutDashboard", null, "main"),
        new("/content", "nav.content", "FileText", PlatformPermissions.ContentRead, "build"),
        new("/media", "nav.media", "Image", PlatformPermissions.MediaRead, "build"),
        new("/sites", "nav.sites", "PanelsTopLeft", PlatformPermissions.SiteEdit, "build"),
        new("/marketplace", "nav.marketplace", "Store", PlatformPermissions.PluginsManage, "build"),
        new("/openapi", "nav.openapi", "FileJson", null, "build"),
        new("/forms", "nav.forms", "Inbox", PlatformPermissions.ContentRead, "main"),
        new("/analytics", "nav.analytics", "TrendingUp", PlatformPermissions.AnalyticsRead, "main"),
        new("/chat", "nav.chat", "MessagesSquare", PlatformPermissions.ChatRead, "main"),
        new("/members", "nav.members", "Users", PlatformPermissions.MembersManage, "admin"),
        new("/roles", "nav.roles", "ShieldCheck", PlatformPermissions.RolesManage, "admin"),
        new("/audit", "nav.audit", "ScrollText", PlatformPermissions.AuditRead, "admin"),
        new("/domains", "nav.domains", "Globe", PlatformPermissions.DomainsManage, "admin"),
        new("/ai", "nav.ai", "Bot", PlatformPermissions.AiSettings, "admin"),
        new("/workspace", "nav.workspace", "Building2", PlatformPermissions.TenantSettings, "admin"),
    ];

    public sealed record NavEntry(
        string To, string LabelKey, string Icon, string? Permission, string Group);

    public static IEndpointRouteBuilder MapNavigationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/navigation", async (
            CurrentUser me, ITenantContext tenant, IPermissionResolver resolver,
            IPluginCatalog catalog, CmsDbContext db, CancellationToken ct) =>
        {
            var userId = me.RequireUserId();

            IReadOnlySet<string> held = me.IsSuperAdmin
                ? PlatformPermissions.All.ToHashSet()
                : tenant.TenantId is { } tid
                    ? await resolver.GetPermissionsAsync(tid, userId, ct)
                    : new HashSet<string>();

            bool May(string? permission) =>
                permission is null || me.IsSuperAdmin || held.Contains(permission);

            var items = Platform
                .Where(e => May(e.Permission))
                .Select(e => new
                {
                    to = e.To,
                    labelKey = e.LabelKey,
                    icon = e.Icon,
                    group = e.Group,
                    // No label: these resolve through the SPA's own locale bundle, which is
                    // where the product's own strings already live.
                    label = (string?)null,
                })
                .ToList();

            /*
             * Plugin instances that declare a page.
             *
             * Only ENABLED ones. A disabled instance keeps its rows and its config so it can be
             * switched back on, but a menu entry leading to a page that answers 404 is worse
             * than no entry.
             *
             * The label is the instance's own name, not the plugin's: a tenant with two
             * galleries needs "Homepage" and "Press kit", and two entries both reading "Image
             * gallery" is the exact ambiguity this endpoint exists to remove.
             */
            var enabled = await db.PluginInstances
                .Where(p => p.Enabled)
                .OrderBy(p => p.Name)
                .Select(p => new { p.PluginId, p.Slug, p.Name })
                .ToListAsync(ct);

            foreach (var instance in enabled)
            {
                var manifest = catalog.Find(instance.PluginId);
                if (manifest?.Nav is not { } nav) continue;

                var permission = nav.Permission.Contains(':')
                    ? nav.Permission
                    : PlatformPermissions.ForPlugin(instance.PluginId, nav.Permission);
                if (!May(permission)) continue;

                items.Add(new
                {
                    to = nav.RouteTemplate.Replace("{instanceSlug}", instance.Slug),
                    labelKey = nav.LabelKey,
                    icon = nav.IconName ?? manifest.IconName ?? "Plug",
                    group = nav.Group,
                    // A tenant-authored name, so it is already in the reader's language and
                    // must NOT go through i18n — there is no key for "Press kit".
                    label = (string?)instance.Name,
                });
            }

            return Results.Ok(new { items });
        }).RequireAuthorization().AllowNonMemberTenant(AllowNonMemberTenantAttribute.SelfScoped);

        return app;
    }
}
