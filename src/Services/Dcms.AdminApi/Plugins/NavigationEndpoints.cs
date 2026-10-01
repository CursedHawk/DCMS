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

        // Gated on the same permission as the model proxy, so the menu never offers a history
        // page to a member who cannot hold a conversation in the first place.
        new("/assistant", "nav.assistant", "Sparkles", PlatformPermissions.SiteEdit, "main"),

        /*
         * One Settings entry, not seven.
         *
         * Members, Roles, Audit log, Domains, AI, Workspace and API Docs each had a line of their
         * own, which grew the menu by one every time the product gained a setting and put "what
         * this workspace is called" beside the work people come here to do. They are sections
         * inside /settings now, and the SPA draws that sub-navigation from its own list — one it
         * can filter without a round trip, because every one of those permissions is already in
         * the caller's set.
         *
         * No permission on the entry: the landing page forwards to the first section the caller
         * may open, and the API reference names none, so it is never a link to nothing.
         */
        new("/settings", "nav.settings", "Settings", null, "admin"),
    ];

    public sealed record NavEntry(
        string To, string LabelKey, string Icon, string? Permission, string Group);

    /// <summary>
    /// A menu entry. Platform entries name an i18n <paramref name="LabelKey"/>; plugin entries
    /// carry their own <paramref name="Label"/> (English) and <paramref name="Labels"/> by language,
    /// because no locale bundle of the console knows a plugin's words. <paramref name="Detail"/>
    /// is the instance a per-instance entry belongs to — a tenant-authored name, never translated.
    /// </summary>
    private sealed record NavItem(
        string To, string? LabelKey, string Icon, string Group, int Order,
        string? Label = null, IReadOnlyDictionary<string, string>? Labels = null, string? Detail = null);

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

            // Platform entries keep their declared order (index × 10), so a plugin places itself
            // between them with AdminNavPlacement.Order.
            var items = Platform
                .Select((e, index) => (Entry: e, Order: index * 10))
                .Where(x => May(x.Entry.Permission))
                .Select(x => new NavItem(x.Entry.To, x.Entry.LabelKey, x.Entry.Icon, x.Entry.Group, x.Order))
                .ToList();

            /*
             * Plugin screens with a menu entry, from ENABLED instances only. A disabled instance
             * keeps its data, config and page (reachable from Plugins), but a switched-off plugin
             * has no business in the menu.
             *
             * A plugin-wide screen appears once while any instance is enabled; an instance
             * screen once per enabled instance, carrying the instance's own name — a tenant with
             * two galleries needs "Homepage" and "Press kit", not two identical entries.
             */
            var enabled = await db.PluginInstances
                .Where(p => p.Enabled)
                .OrderBy(p => p.Name)
                .Select(p => new { p.PluginId, p.Slug, p.Name })
                .ToListAsync(ct);

            foreach (var group in enabled.GroupBy(i => i.PluginId))
            {
                if (catalog.Find(group.Key) is not { } manifest) continue;
                foreach (var screen in manifest.AdminScreens ?? [])
                {
                    if (screen.Nav is not { } nav
                        || !May(PluginPermissions.Resolve(manifest.Id, screen.Permission ?? PlatformPermissions.PluginsManage)))
                    {
                        continue;
                    }
                    var icon = screen.IconName ?? manifest.IconName ?? "Plug";
                    if (screen.Scope == AdminScreenScope.Plugin)
                    {
                        items.Add(new NavItem($"/app/{manifest.Id}/{screen.Id}", null, icon, nav.Group, nav.Order,
                            screen.Title, screen.Titles));
                        continue;
                    }
                    foreach (var instance in group)
                    {
                        items.Add(new NavItem($"/plugins/{instance.Slug}/{screen.Id}", null, icon, nav.Group, nav.Order,
                            screen.Title, screen.Titles, instance.Name));
                    }
                }
            }

            return Results.Ok(new { items = items.OrderBy(i => i.Order).ToList() });
        }).RequireAuthorization().AllowNonMemberTenant(AllowNonMemberTenantAttribute.SelfScoped);

        return app;
    }
}
