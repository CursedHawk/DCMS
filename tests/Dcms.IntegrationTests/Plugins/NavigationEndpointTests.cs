extern alias AdminApiApp;

using AdminApiApp::Dcms.AdminApi.Plugins;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Security;

namespace Dcms.IntegrationTests.Plugins;

/// <summary>
/// The menu is now assembled on the server, which means the mistakes it can make are new ones:
/// an entry gated on a permission string that no role can ever hold is invisible to everybody,
/// including the SuperAdmin who would have to work out why.
/// </summary>
public sealed class NavigationEndpointTests
{
    [Fact]
    public void Every_entry_names_a_real_permission_or_none_at_all()
    {
        foreach (var entry in NavigationEndpoints.Platform)
        {
            if (entry.Permission is null) continue;
            PlatformPermissions.All.Should().Contain(
                entry.Permission,
                $"'{entry.To}' is gated on '{entry.Permission}', which no role can hold");
        }
    }

    [Fact]
    public void Routes_are_unique()
    {
        // Two entries on one route render as two identical menu items, and the active-path
        // rule then lights both.
        NavigationEndpoints.Platform.Select(e => e.To).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Routes_are_absolute()
    {
        NavigationEndpoints.Platform.Should().AllSatisfy(
            e => e.To.Should().StartWith("/", "the SPA router takes absolute paths"));
    }

    [Fact]
    public void Groups_come_from_the_set_the_sidebar_renders()
    {
        // A group the sidebar does not know renders nothing at all — the items are filtered
        // into a heading that is never drawn, so they silently vanish from the menu.
        string[] known = ["overview", "manage", "apps", "extend", "workspace"];
        NavigationEndpoints.Platform.Should().AllSatisfy(
            e => known.Should().Contain(e.Group, $"'{e.To}' is in group '{e.Group}'"));
    }

    private static readonly PluginManifest Crm = PluginManifest.Create("dynamic-apps", "Dynamic Apps", "", true,
        iconName: "DatabaseZap",
        permissions: [new PermissionDefinition("data-read", "Read records")],
        contentTypes: [new ContentTypeDefinition("galleryImage", [], false, null)],
        adminScreens:
        [
            new AdminScreen("inbox", "Inbox", AdminScreenScope.Plugin, Permission: "data-read"),
            new AdminScreen("records", "Records", AdminScreenScope.Instance, Permission: "data-read"),
            new AdminScreen("configuration", "Configuration", AdminScreenScope.Instance) { Section = AdminScreenSection.Admin },
            AdminScreen.Settings,
        ]);

    private static List<NavigationEndpoints.NavItem> AppsFor(params string[] held) =>
        NavigationEndpoints.Apps(
            [("dynamic-apps", "crm", "CRM"), ("dynamic-apps", "hr", "HR desk"), ("gone", "old", "Uninstalled")],
            id => id == Crm.Id ? Crm : null,
            p => p is null || held.Contains(p)).ToList();

    [Fact]
    public void Every_enabled_instance_is_an_app_with_its_work_under_it()
    {
        var apps = AppsFor(PlatformPermissions.PluginsManage, PlatformPermissions.ContentRead, "plugin:dynamic-apps:data-read");

        // One per instance, named by the instance; an instance of a plugin no longer installed is not one.
        apps.Select(a => (a.To, a.Label, a.Detail)).Should().Equal(
            ("/apps/crm", "CRM", "Dynamic Apps"), ("/apps/hr", "HR desk", "Dynamic Apps"));
        apps.Should().AllSatisfy(a => a.Group.Should().Be("apps"));
        // Work only, instance screens first, then the collections; configuration and settings are in the app's Admin group.
        apps[0].Children!.Select(c => (c.To, c.Label)).Should().Equal(
            ("/apps/crm/records", "Records"), ("/apps/crm/inbox", "Inbox"), ("/apps/crm/content/galleryImage", "Gallery Image"));
    }

    [Fact]
    public void A_member_sees_only_the_apps_and_screens_they_may_open()
    {
        var reader = AppsFor("plugin:dynamic-apps:data-read");
        reader.Should().HaveCount(2);
        reader[0].Children!.Select(c => c.To).Should().Equal("/apps/crm/records", "/apps/crm/inbox");

        // Nothing to open in it: no screen, no collection, not a plugin manager.
        AppsFor().Should().BeEmpty();
        // A plugin manager may open its settings, so it is there, with no work under it.
        AppsFor(PlatformPermissions.PluginsManage).Should().AllSatisfy(a => a.Children.Should().BeEmpty());
    }

    [Theory]
    [InlineData("galleryImage", "Gallery Image")]
    [InlineData("blog_post", "Blog post")]
    [InlineData("event", "Event")]
    public void Content_types_are_named_as_the_console_names_them(string name, string expected) =>
        NavigationEndpoints.Humanize(name).Should().Be(expected);

    [Fact]
    public void Installed_plugins_and_the_marketplace_are_both_in_the_menu()
    {
        // /plugins was missing here while the SPA's first-paint list had it, so it was drawn and
        // then removed on every cold load.
        NavigationEndpoints.Platform.Select(e => e.To).Should().Contain(["/plugins", "/marketplace"]);
    }

    [Fact]
    public void Label_keys_follow_the_nav_namespace()
    {
        // The SPA resolves these through its own locale bundle. A key outside `nav.` is one
        // nobody will think to add when they add a destination, and it renders as the raw key.
        NavigationEndpoints.Platform.Should().AllSatisfy(
            e => e.LabelKey.Should().StartWith("nav."));
    }

    [Fact]
    public void Icon_names_are_pascal_case_lucide_names()
    {
        // Resolved verbatim against the lucide export map, with a generic glyph on a miss —
        // so a lowercase name is a silently generic entry rather than an error.
        NavigationEndpoints.Platform.Should().AllSatisfy(
            e => e.Icon.Should().MatchRegex("^[A-Z][A-Za-z0-9]*$"));
    }
}
