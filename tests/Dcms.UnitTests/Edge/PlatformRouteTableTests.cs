using Dcms.Edge;
using Dcms.Edge.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Yarp.ReverseProxy.Configuration;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The edge's route table is the entire public ingress expressed as data, and the failure mode
/// of getting it wrong is traffic silently reaching the wrong service — a 404 on every tenant
/// domain, or admin-api answering for the platform console's own API. The Caddyfile encoded
/// this as block ordering and had no test at all; this is the port's replacement for reading it
/// carefully.
///
/// <para>Paths are matched with ASP.NET's own <see cref="TemplateMatcher"/>, the engine YARP
/// matches with, so these assertions test the patterns rather than a re-implementation of them.
/// Selection then follows YARP's documented contract: among routes whose host and path both
/// match, the lowest <see cref="RouteConfig.Order"/> wins.</para>
/// </summary>
public class PlatformRouteTableTests
{
    private const string Admin = "admin.example.test";
    private const string Platform = "platform.example.test";
    private const string Grafana = "grafana.example.test";
    private const string Git = "git.example.test";

    private static readonly EdgeOptions Options = new()
    {
        AdminHost = Admin,
        PlatformHost = Platform,
        GrafanaHost = Grafana,
        GitHost = Git,
    };

    private static readonly IReadOnlyList<RouteConfig> Routes = PlatformRoutes.Build(Options).Routes;
    private static readonly IReadOnlyList<ClusterConfig> Clusters = PlatformRoutes.Build(Options).Clusters;

    [Theory]
    // ---- Admin host: the four handle blocks of the Caddyfile's admin site, in order ----
    [InlineData(Admin, "/connect/authorize", PlatformRoutes.Identity)]
    [InlineData(Admin, "/connect", PlatformRoutes.Identity)]
    [InlineData(Admin, "/account/login", PlatformRoutes.Identity)]
    [InlineData(Admin, "/signin-google", PlatformRoutes.Identity)]
    [InlineData(Admin, "/.well-known/openid-configuration", PlatformRoutes.Identity)]
    [InlineData(Admin, "/api/admin/domains", PlatformRoutes.AdminApi)]
    [InlineData(Admin, "/hub/chat", PlatformRoutes.ContentApi)]
    [InlineData(Admin, "/", PlatformRoutes.AdminSpa)]
    [InlineData(Admin, "/sites/123/edit", PlatformRoutes.AdminSpa)]
    // ---- Platform host: the two specific /api prefixes must out-rank the general one ----
    [InlineData(Platform, "/api/platform/observability", PlatformRoutes.PlatformApi)]
    [InlineData(Platform, "/api/identity/users", PlatformRoutes.Identity)]
    [InlineData(Platform, "/api/admin/tenants", PlatformRoutes.AdminApi)]
    [InlineData(Platform, "/connect/token", PlatformRoutes.Identity)]
    [InlineData(Platform, "/", PlatformRoutes.PlatformSpa)]
    // ---- The two third-party consoles ----
    [InlineData(Grafana, "/d/abc/dashboard", PlatformRoutes.Grafana)]
    [InlineData(Git, "/dcms/site-1.git/info/refs", PlatformRoutes.Forgejo)]
    // ---- Tenant custom domains: anything not named above ----
    [InlineData("shop.tenant.example", "/", PlatformRoutes.SiteHost)]
    [InlineData("shop.tenant.example", "/api/content/pages", PlatformRoutes.SiteHost)]
    [InlineData("shop.tenant.example", "/connect/authorize", PlatformRoutes.SiteHost)]
    public void Routes_the_hosts_and_paths_the_Caddyfile_did(string host, string path, string expectedCluster)
        => ResolveCluster(host, path).Should().Be(expectedCluster);

    /// <summary>
    /// ASP.NET routing is case-insensitive and the cookie-auth challenge redirects to the
    /// capitalised "/Account/Login" while the endpoints are mapped lowercase. Caddy needed an
    /// explicit <c>(?i)</c> flag for this; route templates need none, and this asserts that
    /// rather than assuming it.
    /// </summary>
    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/ACCOUNT/login")]
    [InlineData("/Connect/Authorize")]
    public void Matches_identity_paths_case_insensitively(string path)
        => ResolveCluster(Admin, path).Should().Be(PlatformRoutes.Identity);

    /// <summary>
    /// On the admin host there is no /api/platform block, so it falls through to admin-api —
    /// exactly as it does under Caddy, where those two handles live only in the platform site.
    /// Asserted so a later "tidy-up" that hoists them to both hosts is a failing test rather
    /// than a change of behaviour nobody noticed.
    /// </summary>
    [Fact]
    public void Platform_console_api_prefixes_are_scoped_to_the_platform_host()
        => ResolveCluster(Admin, "/api/platform/observability").Should().Be(PlatformRoutes.AdminApi);

    /// <summary>
    /// The catch-all must never out-rank a named host, or every operator request would be
    /// handed to site-host and 404 as an unverified domain.
    /// </summary>
    [Fact]
    public void Tenant_catch_all_never_wins_on_a_platform_host()
    {
        foreach (var host in new[] { Admin, Platform, Grafana, Git })
        {
            ResolveCluster(host, "/anything/at/all").Should().NotBe(PlatformRoutes.SiteHost,
                $"{host} is a platform-owned host");
        }
    }

    [Fact]
    public void Every_route_names_a_cluster_that_exists()
    {
        var clusterIds = Clusters.Select(c => c.ClusterId).ToHashSet();
        Routes.Should().OnlyContain(r => clusterIds.Contains(r.ClusterId!));
    }

    /// <summary>YARP rejects a config with duplicate route ids, which would take the edge down.</summary>
    [Fact]
    public void Route_ids_are_unique()
        => Routes.Select(r => r.RouteId).Should().OnlyHaveUniqueItems();

    [Fact]
    public void Every_cluster_has_a_destination()
        => Clusters.Should().OnlyContain(c => c.Destinations != null && c.Destinations.Count > 0);

    /// <summary>
    /// Only the tenant catch-all serves anonymous visitors, and it is the only route on which
    /// tenant-selection headers are scrubbed. If another route ever gains that mark, the
    /// scrubbing has widened to somewhere the admin SPA legitimately sends X-Dcms-Tenant.
    /// </summary>
    [Fact]
    public void Only_the_tenant_catch_all_is_marked_public_plane()
    {
        var marked = Routes
            .Where(r => r.Metadata?.ContainsKey(PlatformRoutes.PublicPlaneMetadataKey) == true)
            .Select(r => r.RouteId);
        marked.Should().BeEquivalentTo(["tenant-sites"]);
    }

    /// <summary>Replicates YARP's selection contract: host and path must both match; lowest Order wins.</summary>
    private static string? ResolveCluster(string host, string path)
        => Routes
            .Where(r => HostMatches(r.Match.Hosts, host) && PathMatches(r.Match.Path!, path))
            .OrderBy(r => r.Order ?? 0)
            .Select(r => r.ClusterId)
            .FirstOrDefault();

    private static bool HostMatches(IReadOnlyList<string>? hosts, string host)
        => hosts is null || hosts.Count == 0
           || hosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    private static bool PathMatches(string template, string path)
    {
        var matcher = new TemplateMatcher(TemplateParser.Parse(template), []);
        return matcher.TryMatch(new PathString(path), new RouteValueDictionary());
    }
}
