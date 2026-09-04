using Microsoft.Extensions.Diagnostics.HealthChecks;
using Yarp.ReverseProxy.Configuration;

namespace Dcms.Edge.Routing;

/// <summary>
/// Reports unhealthy when the edge has no routes.
///
/// <para>The public ingress is the one part of the stack a deploy can break silently: a config
/// that fails to load leaves the container up and every health signal green while nothing is
/// served. A proxy that starts with an empty route table is the
/// same failure in a different language — the process is fine, and it answers every request with
/// a 404.</para>
///
/// <para>Checking that the config is loaded rather than merely that the process is alive is the
/// whole point: it is the difference between "Kestrel is listening" and "the thing that was
/// shipped is the thing that is running".</para>
/// </summary>
public sealed class RouteTableHealthCheck(IProxyConfigProvider provider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var config = provider.GetConfig();
        if (config.Routes.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "The edge has no routes. Every request would 404; this is a broken deploy, not an idle edge."));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"{config.Routes.Count} routes across {config.Clusters.Count} clusters."));
    }
}
