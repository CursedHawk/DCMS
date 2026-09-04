using Dcms.Shared.Data.Edge;
using Microsoft.EntityFrameworkCore;
using Yarp.ReverseProxy.Configuration;

namespace Dcms.Edge.Routing;

/// <summary>
/// Reads the route overlay from <c>edge.routes</c>.
///
/// <para>An overlay, not a replacement: the four operator hosts stay in configuration so the
/// edge serves them before Postgres answers — an edge that cannot serve the admin host until the
/// database is up cannot be used to diagnose a database that is down. This adds what only the
/// running platform knows.</para>
///
/// <para><b>Reads are best-effort and never fatal.</b> A database that is unreachable leaves the
/// static table serving, which is the whole reason the static table exists.</para>
/// </summary>
public sealed class DatabaseRouteSource(IServiceProvider services, ILogger<DatabaseRouteSource> logger)
{
    public IReadOnlyList<RouteConfig> Load(IReadOnlySet<string> knownClusterIds)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
            var rows = db.Routes.AsNoTracking().Where(r => r.Enabled).ToList();

            var routes = new List<RouteConfig>(rows.Count);
            foreach (var row in rows)
            {
                // A row naming a cluster that does not exist is skipped, not applied. YARP
                // validates the whole config as a unit and rejects all of it on one bad
                // reference, so applying this blindly would let a single malformed row take
                // every route down — including the admin host someone would use to fix it.
                if (!knownClusterIds.Contains(row.ClusterId))
                {
                    logger.LogWarning(
                        "Skipping edge route {RouteId}: cluster {ClusterId} is not defined.",
                        row.RouteId, row.ClusterId);
                    continue;
                }

                routes.Add(new RouteConfig
                {
                    RouteId = row.RouteId,
                    ClusterId = row.ClusterId,
                    Order = row.Order,
                    Match = new RouteMatch
                    {
                        Hosts = SplitHosts(row.Hosts),
                        Path = row.PathPattern,
                    },
                });
            }

            return routes;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the edge route overlay; serving the static table only.");
            return [];
        }
    }

    private static string[]? SplitHosts(string? hosts)
        => string.IsNullOrWhiteSpace(hosts)
            ? null
            : [.. hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
