extern alias AdminApiApp;
using System.Text.RegularExpressions;

namespace Dcms.IntegrationTests.Notifications;

/// <summary>
/// Every notification's <c>LinkPath</c> has to name a route the admin SPA actually has. No
/// containers: this is a source scan of the producers against the SPA's route table.
///
/// <para>It exists because a wrong link fails in the least visible way there is. Content
/// notifications pointed at <c>/content/{pluginInstanceId}</c> for as long as they had existed,
/// and there has never been a route of that shape — a content item is edited in a MODAL over
/// <c>/content</c>, not on a page of its own. So the notification arrived, the bell rendered it,
/// the user clicked it, and the SPA said Not Found. Nothing logged, nothing threw, and no test
/// could have caught it because the two halves live in different languages.</para>
///
/// <para>The rule is narrow on purpose: the path <b>before</b> any query string must match a
/// declared route. Query parameters are how a page is told which collection to select and which
/// item to open, and the router does not route on them.</para>
/// </summary>
public class NotificationLinkPathTests
{
    /// <summary>Every distinct literal path a notification producer assigns to <c>LinkPath</c>.</summary>
    public static TheoryData<string, string> ProducedLinkPaths()
    {
        var data = new TheoryData<string, string>();
        foreach (var (path, source) in LinkPaths())
        {
            data.Add(path, source);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ProducedLinkPaths))]
    public void Every_notification_link_points_at_a_route_the_spa_has(string linkPath, string source)
    {
        var routes = SpaRoutes();
        var path = linkPath.Split('?')[0];

        RouteExists(path, routes).Should().BeTrue(
            "{0} raises a notification linking to '{1}', and the admin SPA has no route matching "
            + "'{2}'. Clicking that notification renders Not Found. Declared routes: {3}",
            source, linkPath, path, string.Join(", ", routes));
    }

    [Fact]
    public void The_spa_route_table_was_actually_found()
    {
        // Guards the scan itself. If routes.tsx is restructured so the regexes below match
        // nothing, every assertion above would pass vacuously — the worst possible outcome for
        // a test whose whole job is to catch a link nobody checked.
        SpaRoutes().Should().Contain(["/content", "/media", "/sites/$siteId"]);
        // The nested and generated halves, each of which would otherwise pass vacuously: a
        // Settings section declared through `settingsChild`, and an old URL that exists only as
        // an entry in the redirect map.
        SpaRoutes().Should().Contain(["/settings/members", "/members"]);
        LinkPaths().Should().HaveCountGreaterThan(5);
    }

    /// <summary>
    /// Matches a concrete path against the SPA's route table, treating a <c>$param</c> segment as
    /// a wildcard — <c>/sites/$siteId</c> is what makes <c>/sites/{guid}</c> a real destination.
    /// </summary>
    private static bool RouteExists(string path, IReadOnlyCollection<string> routes)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var route in routes)
        {
            var routeParts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (routeParts.Length != parts.Length) continue;
            if (routeParts.Zip(parts).All(pair => pair.First.StartsWith('$') || pair.First == pair.Second))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The paths declared in <c>apps/admin/src/routes.tsx</c>: the <c>child('/x', …)</c> helper
    /// calls, the <c>settingsChild('x', …)</c> ones (relative to <c>/settings</c>), and any route
    /// declaring <c>path:</c> directly (the parameterised site workspace).
    ///
    /// <para>Plus the old top-level URLs, which are declared as data in
    /// <c>app/routeGuards.ts</c> and turned into redirect routes in a loop, so no literal appears
    /// in <c>routes.tsx</c> at all. They are real destinations — a notification written before
    /// Settings was consolidated still links to <c>/members</c> and still has to work.</para>
    /// </summary>
    private static IReadOnlyList<string> SpaRoutes()
    {
        var admin = Path.Combine(RepoRoot(), "apps", "admin", "src");
        var source = File.ReadAllText(Path.Combine(admin, "routes.tsx"));
        var guards = File.ReadAllText(Path.Combine(admin, "app", "routeGuards.ts"));

        var routes = Regex.Matches(source, @"child\(\s*'(?<path>[^']+)'")
            .Concat(Regex.Matches(source, @"path:\s*'(?<path>[^']+)'"))
            .Select(m => m.Groups["path"].Value)
            .ToList();

        routes.AddRange(Regex.Matches(source, @"settingsChild\(\s*'(?<path>[^']+)'")
            .Select(m => "/settings/" + m.Groups["path"].Value));

        // LEGACY_SETTINGS_PATHS: '/old': '/settings/new'
        routes.AddRange(Regex.Matches(guards, @"'(?<from>/[a-z]+)':\s*'/settings/")
            .Select(m => m.Groups["from"].Value));

        return routes.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Every literal assigned to a <c>LinkPath:</c> argument across the services, with the file
    /// that produced it. Interpolation holes become a <c>$param</c> segment so they match a
    /// parameterised route, which is exactly what <c>/sites/{evt.SiteId}</c> is.
    /// </summary>
    private static List<(string Path, string Source)> LinkPaths()
    {
        var found = new List<(string, string)>();
        var src = Path.Combine(RepoRoot(), "src");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (!text.Contains("LinkPath")) continue;

            foreach (Match match in Regex.Matches(text, @"LinkPath:\s*\$?""(?<value>[^""]*)"""))
            {
                var raw = match.Groups["value"].Value;
                if (raw.Length == 0) continue;
                found.Add((Regex.Replace(raw, @"\{[^}]*\}", "$param"), Path.GetFileName(file)));
            }
        }

        // ContentLink builds its link in a helper rather than inline, so the regex above cannot
        // see it — and it is the one that was wrong. Named explicitly so it is still covered.
        found.Add((AdminApiApp::Dcms.AdminApi.Notifications.ContentLink.For(
            Guid.NewGuid(), "blogPost", Guid.NewGuid()), "ContentNotificationConsumer.cs"));

        return found.Distinct().ToList();
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dcms.sln")))
        {
            directory = directory.Parent;
        }
        Assert.SkipWhen(directory is null, "Repository root not found; the source tree is not available here.");
        return directory!.FullName;
    }
}
