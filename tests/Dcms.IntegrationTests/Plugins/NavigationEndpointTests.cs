extern alias AdminApiApp;

using AdminApiApp::Dcms.AdminApi.Plugins;
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
        string[] known = ["main", "build", "admin", "plugins"];
        NavigationEndpoints.Platform.Should().AllSatisfy(
            e => known.Should().Contain(e.Group, $"'{e.To}' is in group '{e.Group}'"));
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
