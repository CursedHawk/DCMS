using Dcms.Edge;
using Dcms.Edge.Auth;
using Dcms.Edge.Routing;
using Dcms.Edge.Transforms;
using Microsoft.AspNetCore.Routing.Template;
using Yarp.ReverseProxy.Configuration;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// What the edge refuses, and who it vouches for. These are the assertions that stop the
/// header-auth arrangement from becoming an authentication bypass or a git identity hijack.
/// </summary>
public class EdgeGatingTests
{
    private static readonly EdgeOptions Options = new()
    {
        AdminHost = "admin.example",
        PlatformHost = "platform.example",
        GrafanaHost = "grafana.example",
        GitHost = "git.example",
    };

    [Fact]
    public void Grafana_is_refused_at_the_edge_rather_than_by_Grafana()
    {
        var route = Route("grafana", authEnabled: true);

        // The rule Grafana's own role_attribute_path enforced before this. Moved here so an
        // unauthorized request never reaches Grafana at all -- which is also how to verify the
        // gate: with a non-SuperAdmin, Grafana's access log stays empty.
        route.AuthorizationPolicy.Should().Be(EdgePolicies.SuperAdmin);
        route.Metadata!["dcms.identity-headers"].Should().Be(IdentityHeaders.Grafana);
    }

    [Fact]
    public void The_Forgejo_web_UI_asks_only_that_you_are_somebody()
    {
        var route = Route("forgejo", authEnabled: true);

        // Not SuperAdmin: Forgejo's accounts mirror every DCMS user and it does its own
        // per-repository authorization once it knows who is asking. Narrowing it here would lock
        // every site editor out of their own repository.
        route.AuthorizationPolicy.Should().Be(EdgePolicies.SignedIn);
        route.Metadata!["dcms.identity-headers"].Should().Be(IdentityHeaders.Forgejo);
    }

    [Theory]
    [InlineData("/acme/site.git/info/refs")]
    [InlineData("/acme/site/info/refs")]
    [InlineData("/acme/site.git/git-upload-pack")]
    [InlineData("/acme/site.git/git-receive-pack")]
    [InlineData("/acme/site.git/info/lfs/objects/batch")]
    [InlineData("/api/v1/repos/acme/site")]
    public void Git_over_HTTP_is_never_gated_and_never_headed(string path)
    {
        var matched = MatchOnHost(path, "git.example", authEnabled: true);

        matched.Should().NotBeNull();
        // Gating these would break `git clone` over HTTPS for every tenant site. Injecting a
        // header on them would break it worse and silently: the request already carries the
        // per-user Basic credential ForgejoUserSync provisioned, and asserting a browser
        // session's identity on top of it does not add a check, it replaces one -- a push
        // attributed to whoever happens to be signed in in that browser.
        matched!.AuthorizationPolicy.Should().BeNull();
        matched.Metadata.Should().BeNull();
    }

    [Fact]
    public void The_Forgejo_web_UI_still_wins_for_an_ordinary_page()
    {
        MatchOnHost("/acme/site/issues", "git.example", authEnabled: true)!
            .RouteId.Should().Be("forgejo");
    }

    [Fact]
    public void No_route_names_a_policy_when_there_is_no_secret_to_have_one()
    {
        var (routes, _) = PlatformRoutes.Build(Options);

        // YARP validates the config as a WHOLE. A route naming a policy the container never
        // registered rejects the ENTIRE table, not that route -- leaving the edge answering 404
        // for every host on the platform, because an operator console could not be gated.
        routes.Should().OnlyContain(r => r.AuthorizationPolicy == null);
    }

    [Fact]
    public void Nothing_that_serves_a_tenant_is_ever_gated()
    {
        var (routes, _) = PlatformRoutes.Build(Options, authEnabled: true);

        // The catch-all is anonymous visitors on somebody else's domain. A policy here would
        // redirect the public internet to a DCMS login.
        routes.Single(r => r.RouteId == "tenant-sites").AuthorizationPolicy.Should().BeNull();
        // As are the SPAs and the APIs: both consoles run browser-side OIDC and their APIs are
        // bearer-token resource servers, so a cookie policy at the edge would break them and
        // secure nothing that is not already checked behind it.
        foreach (var id in new[] { "admin-spa", "platform-spa", "admin-api", "admin-hub", "platform-api" })
        {
            routes.Single(r => r.RouteId == id).AuthorizationPolicy.Should().BeNull($"{id} is not cookie-authenticated");
        }
    }

    private static RouteConfig Route(string routeId, bool authEnabled)
        => PlatformRoutes.Build(Options, authEnabled).Routes.Single(r => r.RouteId == routeId);

    /// <summary>
    /// The route YARP would pick: every route whose host matches and whose template matches,
    /// lowest Order first. Enough to assert precedence, which is the property under test.
    /// </summary>
    private static RouteConfig? MatchOnHost(string path, string host, bool authEnabled)
        => PlatformRoutes.Build(Options, authEnabled).Routes
            .Where(r => r.Match.Hosts is null || r.Match.Hosts.Contains(host))
            .Where(r => Matches(r.Match.Path!, path))
            .OrderBy(r => r.Order ?? 0)
            .FirstOrDefault();

    private static bool Matches(string template, string path)
    {
        var matcher = new TemplateMatcher(
            TemplateParser.Parse(template), new Microsoft.AspNetCore.Routing.RouteValueDictionary());
        return matcher.TryMatch(path, new Microsoft.AspNetCore.Routing.RouteValueDictionary());
    }
}
