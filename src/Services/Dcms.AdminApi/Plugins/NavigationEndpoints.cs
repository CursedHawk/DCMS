using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Security;
using Dcms.Shared.Security.Authorization;
using Dcms.AdminApi.Tenancy;
using System.Text.RegularExpressions;
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
        new("/", "nav.dashboard", "LayoutDashboard", null, "overview"),

        // Gated on the same permission as the model proxy, so the menu never offers a history
        // page to a member who cannot hold a conversation in the first place.
        new("/assistant", "nav.assistant", "Sparkles", PlatformPermissions.SiteEdit, "overview"),

        new("/sites", "nav.sites", "PanelsTopLeft", PlatformPermissions.SiteEdit, "manage"),
        new("/content", "nav.content", "FileText", PlatformPermissions.ContentRead, "manage"),
        new("/media", "nav.media", "Image", PlatformPermissions.MediaRead, "manage"),

        // The installed instances and the shopfront are two different questions — "what do I
        // have" and "what could I add" — and the first one was missing from this list, so it was
        // drawn on first paint from the SPA's fallback and then vanished when this answered.
        new("/plugins", "nav.installed", "Plug", PlatformPermissions.PluginsManage, "extend"),
        new("/marketplace", "nav.marketplace", "Store", PlatformPermissions.PluginsManage, "extend"),

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
        new("/settings", "nav.settings", "Settings", null, "workspace"),
    ];

    public sealed record NavEntry(
        string To, string LabelKey, string Icon, string? Permission, string Group);

    /// <summary>
    /// A menu entry. Platform entries name an i18n <paramref name="LabelKey"/>; an app carries its
    /// instance's name as <paramref name="Label"/> (tenant-authored, never translated), the plugin's
    /// name as <paramref name="Detail"/>, and its work screens as <paramref name="Children"/>.
    /// </summary>
    public sealed record NavItem(
        string To, string? LabelKey, string Icon, string Group,
        string? Label = null, string? Detail = null, IReadOnlyList<NavChild>? Children = null);

    /// <summary>A page of an app, for the command palette. <paramref name="Labels"/> by language, as the plugin declares them.</summary>
    public sealed record NavChild(string To, string Label, IReadOnlyDictionary<string, string>? Labels = null);

    /// <summary>
    /// One app per enabled instance (#337), in name order, for a member who may open any of it:
    /// one of its screens, one of its content collections, or — holding plugins:manage — its
    /// settings and the platform's pages of it. Under it, the work it holds: the plugin's Work
    /// screens (plugin-wide ones too, which show the app's instance) and its content collections.
    /// </summary>
    public static IEnumerable<NavItem> Apps(
        IEnumerable<(string PluginId, string Slug, string Name)> enabled,
        Func<string, PluginManifest?> find,
        Func<string?, bool> may)
    {
        foreach (var instance in enabled)
        {
            if (find(instance.PluginId) is not { } manifest) continue;
            var screens = (manifest.AdminScreens ?? [])
                .Where(s => may(PluginPermissions.Resolve(manifest.Id, s.Permission ?? PlatformPermissions.PluginsManage)))
                .ToList();
            var children = screens
                .Where(s => s.Section == AdminScreenSection.Work)
                .OrderBy(s => s.Scope == AdminScreenScope.Instance ? 0 : 1)
                .Select(s => new NavChild($"/apps/{instance.Slug}/{s.Id}", s.Title, s.Titles))
                .Concat(may(PlatformPermissions.ContentRead)
                    ? manifest.ContentTypes.Select(ct => new NavChild($"/apps/{instance.Slug}/content/{ct.Name}", Humanize(ct.Name)))
                    : [])
                .ToList();
            if (children.Count == 0 && screens.Count == 0 && !may(PlatformPermissions.PluginsManage)) continue;
            yield return new NavItem($"/apps/{instance.Slug}", null, manifest.IconName ?? "Plug", "apps",
                instance.Name, manifest.Name, children);
        }
    }

    /// <summary><c>galleryImage</c> → "Gallery Image", as the console names a content type.</summary>
    public static string Humanize(string name)
    {
        var spaced = Regex.Replace(Regex.Replace(name, "[_-]+", " "), "([a-z0-9])([A-Z])", "$1 $2").Trim();
        return spaced.Length == 0 ? spaced : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

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

            // Platform entries in their declared order; the apps after them.
            var items = Platform
                .Where(e => May(e.Permission))
                .Select(e => new NavItem(e.To, e.LabelKey, e.Icon, e.Group))
                .ToList();

            // Apps after the platform's entries; a disabled instance keeps its data and config
            // (reachable from Installed) but has no business in the menu.
            var enabled = await db.PluginInstances
                .Where(p => p.Enabled)
                .OrderBy(p => p.Name)
                .Select(p => new { p.PluginId, p.Slug, p.Name })
                .ToListAsync(ct);
            items.AddRange(Apps(enabled.Select(i => (i.PluginId, i.Slug, i.Name)), catalog.Find, May));

            return Results.Ok(new { items });
        }).RequireAuthorization().AllowNonMemberTenant(AllowNonMemberTenantAttribute.SelfScoped);

        return app;
    }
}
