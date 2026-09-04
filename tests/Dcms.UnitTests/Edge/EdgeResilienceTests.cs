using Dcms.Edge;
using Dcms.Edge.Protection;
using Dcms.Edge.Routing;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.SessionAffinity;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The Phase 6 cluster policy, and the two ways it could quietly cause the outage it exists to
/// prevent.
/// </summary>
public class EdgeResilienceTests
{
    [Fact]
    public void A_single_destination_gets_no_health_check_to_fail()
    {
        var clusters = PlatformRoutes.Build(new EdgeOptions()).Clusters;

        // With one destination a health policy routes around nothing: marking it unhealthy makes
        // the edge answer 503 for a service that is merely slow, and hides the real response the
        // client would have seen. A flapping probe becomes a hard failure -- so nothing here is
        // attached until there is somewhere to fail over TO.
        clusters.Should().OnlyContain(c => c.Destinations!.Count == 1);
        clusters.Should().OnlyContain(c => c.HealthCheck == null);
        clusters.Should().OnlyContain(c => c.LoadBalancingPolicy == null);
        clusters.Should().OnlyContain(c => c.SessionAffinity == null);
    }

    [Fact]
    public void A_second_address_switches_the_whole_policy_on()
    {
        var options = new EdgeOptions();
        options.Upstreams.ContentApi = "http://content-api-1:8080,http://content-api-2:8080";

        var cluster = PlatformRoutes.Build(options).Clusters
            .Single(c => c.ClusterId == PlatformRoutes.ContentApi);

        cluster.Destinations!.Should().HaveCount(2);
        cluster.HealthCheck!.Active!.Enabled.Should().BeTrue();
        cluster.HealthCheck.Passive!.Enabled.Should().BeTrue();
        cluster.LoadBalancingPolicy.Should().NotBeNull();

        // The liveness probe, not the full one. A replica whose database is unreachable is not a
        // replica to route around, because so is every other -- and taking them all out of
        // rotation converts a database blip into a total outage.
        cluster.HealthCheck.Active.Path.Should().Be("/health/live");
    }

    [Fact]
    public void Only_the_hub_bearing_cluster_gets_session_affinity()
    {
        var options = new EdgeOptions();
        options.Upstreams.ContentApi = "http://content-api-1:8080,http://content-api-2:8080";
        options.Upstreams.AdminApi = "http://admin-api-1:8080,http://admin-api-2:8080";

        var clusters = PlatformRoutes.Build(options).Clusters;

        // SignalR's negotiate and the connection that follows must reach the same replica.
        // Without a backplane, a WebSocket that lands elsewhere establishes and then receives
        // nothing -- which presents as "chat is broken sometimes" rather than as an error.
        clusters.Single(c => c.ClusterId == PlatformRoutes.ContentApi)
            .SessionAffinity!.Policy.Should().Be(SessionAffinityConstants.Policies.Cookie);
        // And nowhere else: pinning stateless API traffic to one replica gives up the load
        // balancing that was the point of adding a second one.
        clusters.Single(c => c.ClusterId == PlatformRoutes.AdminApi)
            .SessionAffinity.Should().BeNull();
    }

    [Fact]
    public void The_sign_in_surface_is_limited_harder_than_everything_else()
    {
        var route = PlatformRoutes.Build(new EdgeOptions()).Routes.Single(r => r.RouteId == "auth");

        route.RateLimiterPolicy.Should().Be(EdgeRateLimiting.AuthPolicy);
    }

    [Fact]
    public void No_route_names_a_cache_policy_that_was_not_registered()
    {
        // YARP validates the config as a WHOLE. A route naming a policy the container does not
        // have rejects the entire table, so the edge answers 404 for every host on the platform
        // -- because an optional cache was left switched off.
        PlatformRoutes.Build(new EdgeOptions(), authEnabled: true, cacheEnabled: false)
            .Routes.Should().OnlyContain(r => r.OutputCachePolicy == null);

        PlatformRoutes.Build(new EdgeOptions(), authEnabled: true, cacheEnabled: true)
            .Routes.Single(r => r.RouteId == "tenant-sites")
            .OutputCachePolicy.Should().Be(EdgeOutputCache.PublicPolicy);
    }

    [Fact]
    public void Only_the_tenant_plane_is_ever_cached()
    {
        var routes = PlatformRoutes.Build(new EdgeOptions(), authEnabled: true, cacheEnabled: true).Routes;

        // Caching an operator console, an API or the token endpoint would serve one operator's
        // response to the next. The catch-all is the only route serving anonymous visitors
        // identical published pages.
        routes.Where(r => r.OutputCachePolicy != null)
            .Should().ContainSingle().Which.RouteId.Should().Be("tenant-sites");
    }
}
