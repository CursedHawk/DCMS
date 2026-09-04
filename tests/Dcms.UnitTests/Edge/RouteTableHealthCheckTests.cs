using Dcms.Edge.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using Yarp.ReverseProxy.Configuration;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// Why the edge's readiness probe asserts a route table rather than a live process.
/// </summary>
public class RouteTableHealthCheckTests
{
    [Fact]
    public async Task Reports_unhealthy_when_the_edge_has_no_routes()
    {
        var check = new RouteTableHealthCheck(Provider([], []));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        // The failure a liveness ping cannot see:
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
