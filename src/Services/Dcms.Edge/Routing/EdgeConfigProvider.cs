using Dcms.Edge.Auth;
using Dcms.Edge.Protection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace Dcms.Edge.Routing;

/// <summary>
/// Supplies YARP's route table, and swaps it at runtime without a restart.
///
/// <para>This is what replaces the bind-mounted Caddyfile. Changing that file needed a
/// container <i>restart</i> — a plain reload re-read a stale inode, which is a trap this stack
/// has already been caught by once. Here a change is a call to <see cref="Reload"/>: YARP
/// re-reads the config through the change token, validates it, and swaps it in atomically. A
/// config that fails validation is rejected and the previous one keeps serving, so a bad route
/// cannot take the edge down.</para>
///
/// <para><b>Composed from sources, most-trusted first.</b> Today the only source is
/// <see cref="PlatformRoutes"/>, built from configuration so the edge can serve the operator
/// hosts before Postgres is reachable. Phase 2 adds a database-backed source layered on top for
/// per-tenant routing; <see cref="Reload"/> and the change token exist now so that addition is
/// a new source rather than a new mechanism.</para>
/// </summary>
public sealed class EdgeConfigProvider : IProxyConfigProvider
{
    private readonly IOptionsMonitor<EdgeOptions> options;
    private readonly DatabaseRouteSource databaseRoutes;
    private readonly bool authEnabled;
    private readonly bool cacheEnabled;
    private readonly ILogger<EdgeConfigProvider> logger;
    private readonly Lock gate = new();
    private volatile EdgeConfig current;

    public EdgeConfigProvider(
        IOptionsMonitor<EdgeOptions> options,
        DatabaseRouteSource databaseRoutes,
        IOptions<EdgeAuthOptions> auth,
        IConfiguration configuration,
        ILogger<EdgeConfigProvider> logger)
    {
        this.options = options;
        this.databaseRoutes = databaseRoutes;
        // Read once, not per rebuild. A route naming a policy the container does not have makes
        // YARP reject the config as a WHOLE -- every route, not just that one -- and the edge is
        // left serving 404s. The flag and the policy registration must come from the same
        // reading of configuration, so neither can move without the other.
        authEnabled = auth.Value.Enabled;
        // Same coupling, same reason: an OutputCachePolicy naming a policy nobody registered
        // fails validation of the whole table, not of that route.
        cacheEnabled = EdgeOutputCache.IsEnabled(configuration);
        this.logger = logger;
        current = BuildConfig(options.CurrentValue);
    }

    public IProxyConfig GetConfig() => current;

    /// <summary>
    /// Rebuilds the route table and signals YARP to pick it up. Safe to call concurrently and
    /// safe to call when nothing has changed — YARP re-validates and swaps either way.
    /// </summary>
    public void Reload()
    {
        EdgeConfig previous;
        lock (gate)
        {
            previous = current;
            current = BuildConfig(options.CurrentValue);
        }

        // Signalled outside the lock: YARP calls GetConfig() synchronously from the callback,
        // and it must see the new value rather than block on the writer that produced it.
        previous.SignalChange();
        logger.LogInformation(
            "Edge route table reloaded: {RouteCount} routes across {ClusterCount} clusters.",
            current.Routes.Count, current.Clusters.Count);
    }

    private EdgeConfig BuildConfig(EdgeOptions edgeOptions)
    {
        var (staticRoutes, clusters) = PlatformRoutes.Build(edgeOptions, authEnabled, cacheEnabled);

        // Static first, overlay second. A database row may not replace a platform route: the
        // operator hosts are how the platform is administered, and a route table that can lock
        // an operator out of the console that edits it is a table nobody should be editing.
        var clusterIds = clusters.Select(c => c.ClusterId).ToHashSet(StringComparer.Ordinal);
        var staticIds = staticRoutes.Select(r => r.RouteId).ToHashSet(StringComparer.Ordinal);

        var routes = new List<RouteConfig>(staticRoutes);
        foreach (var overlay in databaseRoutes.Load(clusterIds))
        {
            if (staticIds.Contains(overlay.RouteId))
            {
                logger.LogWarning(
                    "Ignoring edge route {RouteId}: it collides with a platform route.", overlay.RouteId);
                continue;
            }
            routes.Add(overlay);
        }

        return new EdgeConfig(routes, clusters);
    }

    private sealed class EdgeConfig : IProxyConfig
    {
        private readonly CancellationTokenSource cts = new();

        public EdgeConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
        {
            Routes = routes;
            Clusters = clusters;
            ChangeToken = new CancellationChangeToken(cts.Token);
        }

        public IReadOnlyList<RouteConfig> Routes { get; }
        public IReadOnlyList<ClusterConfig> Clusters { get; }
        public IChangeToken ChangeToken { get; }

        public void SignalChange() => cts.Cancel();
    }
}
