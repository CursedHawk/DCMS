using Dcms.Edge.Auth;
using Dcms.Edge.Protection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace Dcms.Edge.Routing;

/// <summary>
/// Supplies YARP's route table, and swaps it at runtime without a restart.
///
/// <para>The route table is data, not a bind-mounted config file. A file-based edge needed a
/// container <i>restart</i> to pick one up — a plain reload re-read a stale inode, a trap this
/// stack has been caught by. Here a change is a call to <see cref="Reload"/>: YARP
/// re-reads the config through the change token, validates it, and swaps it in atomically. A
/// config that fails validation is rejected and the previous one keeps serving, so a bad route
/// cannot take the edge down.</para>
///
/// <para>The route table is built from configuration (<see cref="PlatformRoutes"/>) so the edge
/// can serve the operator hosts before Postgres is reachable. <see cref="Reload"/> and the change
/// token let it be swapped at runtime as options change, without a restart.</para>
/// </summary>
public sealed class EdgeConfigProvider : IProxyConfigProvider
{
    private readonly IOptionsMonitor<EdgeOptions> options;
    private readonly bool authEnabled;
    private readonly bool cacheEnabled;
    private readonly ILogger<EdgeConfigProvider> logger;
    private readonly Lock gate = new();
    private volatile EdgeConfig current;

    public EdgeConfigProvider(
        IOptionsMonitor<EdgeOptions> options,
        IOptions<EdgeAuthOptions> auth,
        IConfiguration configuration,
        ILogger<EdgeConfigProvider> logger)
    {
        this.options = options;
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
        return new EdgeConfig(staticRoutes, clusters);
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
