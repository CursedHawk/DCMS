using Dcms.Edge.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using Yarp.ReverseProxy.Configuration;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The check that replaces Caddy's probe, and the reason it exists rather than a liveness ping.
/// </summary>
public class RouteTableHealthCheckTests
{
    [Fact]
    public async Task Reports_unhealthy_when_the_edge_has_no_routes()
    {
        var check = new RouteTableHealthCheck(Provider([], []));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        // This is the failure Caddy's healthcheck comment warned about, in a different language:
        // the process is fine, Kestrel is listening, and every request gets a 404. Nothing else
        // about the container says so, which is exactly why a deploy could ship it unnoticed.
        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Reports_healthy_once_the_table_is_loaded()
    {
        var routes = new[] { new RouteConfig { RouteId = "admin-spa", ClusterId = "admin-spa", Match = new RouteMatch { Path = "/{**catch-all}" } } };
        var clusters = new[] { new ClusterConfig { ClusterId = "admin-spa" } };

        var result = await new RouteTableHealthCheck(Provider(routes, clusters))
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.Should().Be(HealthStatus.Healthy);
        // The counts are in the description so an operator reading a failing probe can tell
        // "the database overlay did not load" from "nothing loaded at all".
        result.Description.Should().Contain("1 routes");
    }

    private static IProxyConfigProvider Provider(
        IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        var config = Substitute.For<IProxyConfig>();
        config.Routes.Returns(routes);
        config.Clusters.Returns(clusters);
        config.ChangeToken.Returns(new CancellationChangeToken(CancellationToken.None));

        var provider = Substitute.For<IProxyConfigProvider>();
        provider.GetConfig().Returns(config);
        return provider;
    }
}
