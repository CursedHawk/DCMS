using Yarp.ReverseProxy.Configuration;

namespace Dcms.Edge.Routing;

/// <summary>
/// The platform-plane route table, ported one-for-one from the site blocks in
/// <c>infra/caddy/Caddyfile</c>. These routes exist before Postgres is reachable, which is why
/// they are built from <see cref="EdgeOptions"/> rather than read from the database: an edge
/// that cannot serve the admin host until the database answers cannot be used to diagnose a
/// database that is not answering.
///
/// <para><b>Order is load-bearing and therefore explicit.</b> The Caddyfile encoded precedence
/// as the order of <c>handle</c> blocks inside a site block — <c>/api/platform/*</c> before
/// <c>/api/*</c>, or admin-api answers for the console's own API. YARP would otherwise decide
/// by its own precedence rules, so every route here states its <see cref="RouteConfig.Order"/>
/// (lower wins) instead of relying on them.</para>
/// </summary>
public static class PlatformRoutes
{
    // Cluster ids. Also the destination keys, since each cluster has exactly one destination
    // today; Phase 6 adds real multi-destination load balancing.
    public const string Identity = "identity";
    public const string AdminApi = "admin-api";
    public const string PlatformApi = "platform-api";
    public const string ContentApi = "content-api";
    public const string SiteHost = "site-host";
    public const string AdminSpa = "admin-spa";
    public const string PlatformSpa = "platform-spa";
    public const string Grafana = "grafana";
    public const string Forgejo = "forgejo";

    /// <summary>
    /// Route metadata key marking a route as serving the <b>public</b> plane — an anonymous
    /// visitor on someone else's domain, rather than an authenticated operator on ours. Read by
    /// <c>PublicPlaneHeaderScrubber</c>, which strips the headers a visitor must not be able to
    /// choose. See that class for what and why.
    /// </summary>
    public const string PublicPlaneMetadataKey = "dcms.public-plane";

    /// <summary>
    /// Identity-owned path prefixes: OIDC discovery/JWKS, the connect/* protocol endpoints, the
    /// interactive login UI, and the Google external-login callback (/signin-google, the
    /// GoogleHandler's default CallbackPath).
    ///
    /// <para>The Caddyfile matched these with a case-insensitive regex because ASP.NET routing
    /// is case-insensitive and the cookie-auth challenge redirects to the capitalised
    /// "/Account/Login" while the endpoints are mapped lowercase. ASP.NET route templates — what
    /// YARP matches with — are case-insensitive by default, so the port needs no equivalent of
    /// the <c>(?i)</c> flag.</para>
    /// </summary>
    private static readonly string[] IdentityPrefixes = ["/connect", "/account", "/.well-known"];

    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Build(EdgeOptions options)
    {
        var admin = options.AdminHost;
        var platform = options.PlatformHost;
        var routes = new List<RouteConfig>();

        // ---- Identity, on both operator hosts (Caddy: the @identity matcher) ----
        //
        // Both hosts on one route set: the SPA and the console are same-origin with identity so
        // their OIDC redirects and token POSTs need no CORS anywhere, which is the established
        // pattern on this stack.
        routes.AddRange(PrefixRoutes("identity", [admin, platform], IdentityPrefixes, Identity, order: 10));
        routes.Add(new RouteConfig
        {
            RouteId = "identity-signin-google",
            ClusterId = Identity,
            Order = 10,
            Match = new RouteMatch { Hosts = [admin, platform], Path = "/signin-google" },
        });

        // ---- Platform console APIs (Caddy: the platform.* site block) ----
        //
        // The two specific prefixes MUST out-rank the general /api below, or admin-api answers
        // for all three.
        routes.Add(Prefix("platform-api", [platform], "/api/platform", PlatformApi, order: 20));
        routes.Add(Prefix("platform-identity-api", [platform], "/api/identity", Identity, order: 21));

        // ---- Admin REST API, on both operator hosts ----
        // Tenancy and the audit log, which admin-api owns, are what the console reaches here.
        routes.Add(Prefix("admin-api", [admin, platform], "/api", AdminApi, order: 30));

        // ---- SignalR hubs hosted by content-api, on the admin host ----
        // Same-origin so the SPA's negotiate + WebSocket avoid CORS. YARP proxies the upgrade.
        routes.Add(Prefix("admin-hub", [admin], "/hub", ContentApi, order: 31));

        // ---- Static SPAs and the two third-party consoles (nginx does SPA fallback) ----
        routes.Add(CatchAll("admin-spa", [admin], AdminSpa, order: 50));
        routes.Add(CatchAll("platform-spa", [platform], PlatformSpa, order: 50));
        routes.Add(CatchAll("grafana", [options.GrafanaHost], Grafana, order: 50));
        routes.Add(CatchAll("forgejo", [options.GitHost], Forgejo, order: 50));

        // ---- Tenant custom domains: everything else ----
        //
        // No Hosts filter and the highest Order, so it is reached only when no named host above
        // matched — the Caddyfile's `https://` catch-all block. site-host resolves the Host
        // header to a tenant's active build and refuses anything unverified, unlinked,
        // unpublished or suspended, so an unknown hostname arriving here is a 404, not a leak.
        routes.Add(new RouteConfig
        {
            RouteId = "tenant-sites",
            ClusterId = SiteHost,
            Order = 100,
            Match = new RouteMatch { Path = "/{**catch-all}" },
            Metadata = new Dictionary<string, string> { [PublicPlaneMetadataKey] = "true" },
        });

        var u = options.Upstreams;
        IReadOnlyList<ClusterConfig> clusters =
        [
            Cluster(Identity, u.Identity),
            Cluster(AdminApi, u.AdminApi),
            Cluster(PlatformApi, u.PlatformApi),
            Cluster(ContentApi, u.ContentApi),
            Cluster(SiteHost, u.SiteHost),
            Cluster(AdminSpa, u.AdminSpa),
            Cluster(PlatformSpa, u.PlatformSpa),
            Cluster(Grafana, u.Grafana),
            Cluster(Forgejo, u.Forgejo),
        ];

        return (routes, clusters);
    }

    private static IEnumerable<RouteConfig> PrefixRoutes(
        string idPrefix, string[] hosts, IReadOnlyList<string> paths, string clusterId, int order)
        => paths.Select((path, i) => Prefix($"{idPrefix}-{i}", hosts, path, clusterId, order));

    /// <summary>
    /// A prefix match. <c>/{prefix}/{**catch-all}</c> also matches the bare <c>/{prefix}</c> —
    /// an ASP.NET catch-all parameter matches the empty string and the trailing separator is
    /// optional — which is what the Caddyfile's <c>(/|$)</c> alternation expressed.
    /// </summary>
    private static RouteConfig Prefix(string routeId, string[] hosts, string prefix, string clusterId, int order)
        => new()
        {
            RouteId = routeId,
            ClusterId = clusterId,
            Order = order,
            Match = new RouteMatch { Hosts = hosts, Path = $"{prefix}/{{**catch-all}}" },
        };

    private static RouteConfig CatchAll(string routeId, string[] hosts, string clusterId, int order)
        => new()
        {
            RouteId = routeId,
            ClusterId = clusterId,
            Order = order,
            Match = new RouteMatch { Hosts = hosts, Path = "/{**catch-all}" },
        };

    private static ClusterConfig Cluster(string clusterId, string address)
        => new()
        {
            ClusterId = clusterId,
            Destinations = new Dictionary<string, DestinationConfig>
            {
                [clusterId] = new DestinationConfig { Address = address },
            },
        };
}
