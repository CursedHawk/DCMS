using Dcms.Edge.Auth;
using Dcms.Edge.Protection;
using Dcms.Edge.Transforms;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.LoadBalancing;
using Yarp.ReverseProxy.SessionAffinity;

namespace Dcms.Edge.Routing;

/// <summary>
/// The platform-plane route table. These routes exist before Postgres is reachable, which is why
/// they are built from <see cref="EdgeOptions"/> rather than read from the database: an edge
/// that cannot serve the admin host until the database answers cannot be used to diagnose a
/// database that is not answering.
///
/// <para><b>Order is load-bearing and therefore explicit.</b> Every prefix here must out-rank
/// the host's SPA catch-all, or the console answers its own API calls with index.html — a
/// <c>200 text/html</c> the http client hands to the query as a string, which renders as a blank
/// page with nothing in any log. YARP would otherwise decide by its own precedence rules, so
/// every route states its <see cref="RouteConfig.Order"/> (lower wins) instead of relying on
/// them.</para>
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
    /// Forgejo's git-over-HTTP and API surface, which must NEVER be given an identity header.
    ///
    /// <para>These requests already carry a credential: the per-user Basic password
    /// <c>ForgejoUserSync</c> provisions, or an API token. Asserting a browser session's identity
    /// on top of that does not add a check, it REPLACES one — a `git push` would be attributed
    /// to whoever happens to be signed in in that browser rather than to the credential the
    /// client presented. In a server holding every tenant's site repositories, that is a commit
    /// under someone else's name and a permission check against the wrong account.</para>
    ///
    /// <para>Ordered ahead of the web-UI catch-all so they win. <c>{repo}</c> captures a
    /// <c>.git</c> suffix on its own, so both URL shapes are covered by one template.
    /// <see cref="IdentityHeaders"/> refuses to inject on any request carrying an
    /// <c>Authorization</c> header as well, because getting this list wrong is the expensive
    /// direction and one guard is not enough for it.</para>
    /// </summary>
    private static readonly string[] ForgejoGitPaths =
    [
        "/{owner}/{repo}/info/refs",
        "/{owner}/{repo}/git-upload-pack",
        "/{owner}/{repo}/git-receive-pack",
        "/{owner}/{repo}/git-upload-archive",
        "/{owner}/{repo}/info/lfs/{**catch-all}",
        "/{owner}/{repo}/objects/{**catch-all}",
        "/api/v1/{**catch-all}",
        "/api/internal/{**catch-all}",
    ];

    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Build(
        EdgeOptions options, bool authEnabled = false, bool cacheEnabled = false)
    {
        var admin = options.AdminHost;
        var platform = options.PlatformHost;
        var routes = new List<RouteConfig>();

        // ---- Authentication, on its own host ----
        //
        // Everything identity serves, and nowhere else: the OIDC protocol endpoints, discovery
        // and JWKS, the interactive login pages, and the external-login callbacks. The edge
        // that came before carved /connect, /account and /.well-known out of the admin and
        // platform hosts instead, which made the issuer a path prefix on the console's name.
        //
        // A whole host rather than a prefix, because the issuer is a claim in every token this
        // platform mints: it is what every resource server validates against, and it should name
        // the thing that does authentication rather than the thing that happens to sit at the
        // same address. It also means the login pages cannot be reached on a host that serves
        // anything else, which is one fewer way for a session cookie to be scoped too widely.
        //
        // The cost is CORS: the two SPAs are no longer same-origin with the token endpoint, so
        // identity's Cors:AllowedOrigins has to name them. That list already existed for the
        // dev split-port case; production now genuinely depends on it.
        routes.Add(CatchAll("auth", [options.AuthHost], Identity, order: 50) with
        {
            // The one surface where the attack is cheap and the prize is an account. Everything
            // else here is limited to keep a service standing up; this is limited to make
            // credential stuffing slow.
            RateLimiterPolicy = EdgeRateLimiting.AuthPolicy,
        });

        // ---- Platform console APIs (the platform.* host) ----
        //
        // Two prefixes, and NO general /api. The console reaches admin-api through neither: it
        // asks platform-api, which checks the operator's platform permission and forwards on a
        // service token. So the console host serves platform-api and identity, and anything else
        // under /api falls through to the SPA catch-all below.
        //
        // That fall-through is why removing this route was the LAST step of the split rather
        // than the first: a browser holding a bundle that still calls /api/admin/... gets
        // `200 text/html` from the SPA, and an http client hands that to the query as a string.
        // A blank page, no error, nothing in a log. Every earlier step had to land, and the
        // scope had to be taken away (6/7), before this was safe.
        routes.Add(Prefix("platform-api", [platform], "/api/platform", PlatformApi, order: 20));
        // Stays on the console's own host: this is the user DIRECTORY, not authentication, and
        // the console calls it same-origin with a bearer token like any other API.
        routes.Add(Prefix("platform-identity-api", [platform], "/api/identity", Identity, order: 21));

        // ---- Admin REST API, on the admin host ----
        routes.Add(Prefix("admin-api", [admin], "/api", AdminApi, order: 30));

        // ---- SignalR hubs hosted by content-api, on the admin host ----
        // Same-origin so the SPA's negotiate + WebSocket avoid CORS. YARP proxies the upgrade.
        routes.Add(Prefix("admin-hub", [admin], "/hub", ContentApi, order: 31));

        // ---- Static SPAs and the two third-party consoles (nginx does SPA fallback) ----
        routes.Add(CatchAll("platform-spa", [platform], PlatformSpa, order: 50));
        routes.Add(CatchAll("admin-spa", [admin], AdminSpa, order: 50));

        // ---- Grafana: gated at the edge, and signed in by header ----
        //
        // Before this, an unauthorized request reached Grafana and Grafana decided. Now it is
        // refused here, so a non-SuperAdmin never appears in Grafana's access log at all --
        // which is also the thing to check when verifying the gate actually gates.
        var grafana = CatchAll("grafana", [options.GrafanaHost], Grafana, order: 50);
        if (authEnabled)
        {
            grafana = grafana with
            {
                AuthorizationPolicy = EdgePolicies.SuperAdmin,
                Metadata = new Dictionary<string, string> { [IdentityHeaders.MetadataKey] = IdentityHeaders.Grafana },
            };
        }
        routes.Add(grafana);

        // ---- Forgejo: git first, then the web UI ----
        //
        // The git and API paths carry their own credential and are left entirely alone: no
        // policy, no header. Gating them would break `git clone` over HTTPS for every tenant
        // site, and heading them would break it worse -- silently, by pushing as the wrong user.
        routes.AddRange(ForgejoGitPaths.Select((path, i) => new RouteConfig
        {
            RouteId = $"forgejo-git-{i}",
            ClusterId = Forgejo,
            Order = 45,
            Match = new RouteMatch { Hosts = [options.GitHost], Path = path },
        }));

        var forgejo = CatchAll("forgejo", [options.GitHost], Forgejo, order: 50);
        if (authEnabled)
        {
            forgejo = forgejo with
            {
                // Signed in as anybody, not SuperAdmin: Forgejo's accounts mirror every DCMS
                // user, and it does its own per-repository authorization once it knows who is
                // asking. The edge's job here is to answer that question, not to narrow it.
                AuthorizationPolicy = EdgePolicies.SignedIn,
                Metadata = new Dictionary<string, string> { [IdentityHeaders.MetadataKey] = IdentityHeaders.Forgejo },
            };
        }
        routes.Add(forgejo);

        // ---- Tenant custom domains: everything else ----
        //
        // No Hosts filter and the highest Order, so it is reached only when no named host above
        // matched. site-host resolves the Host
        // header to a tenant's active build and refuses anything unverified, unlinked,
        // unpublished or suspended, so an unknown hostname arriving here is a 404, not a leak.
        routes.Add(new RouteConfig
        {
            RouteId = "tenant-sites",
            ClusterId = SiteHost,
            Order = 100,
            Match = new RouteMatch { Path = "/{**catch-all}" },
            Metadata = new Dictionary<string, string> { [PublicPlaneMetadataKey] = "true" },
            // Only when the cache is registered. Naming a policy the container does not have
            // fails YARP's validation of the config as a whole, which would leave the edge with
            // an empty route table and every host on the platform a 404 -- the same coupling the
            // authorization policies have, for the same reason.
            OutputCachePolicy = cacheEnabled ? EdgeOutputCache.PublicPolicy : null,
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

    /// <summary>
    /// A prefix match. <c>/{prefix}/{**catch-all}</c> also matches the bare <c>/{prefix}</c> —
    /// an ASP.NET catch-all parameter matches the empty string and the trailing separator is
    /// optional — so the bare prefix does not need a route of its own.
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

    /// <summary>
    /// A cluster, from one address or several comma-separated ones.
    ///
    /// <para><b>Health checks, load balancing and session affinity are attached only when there
    /// is more than one destination, and that is a correctness decision rather than an
    /// optimisation.</b> With a single destination every one of them is an outage amplifier: a
    /// health policy that marks the only destination unhealthy does not route around anything,
    /// it makes the edge answer 503 for a service that is merely slow — turning a flapping probe
    /// into a hard failure, and hiding the real response the client would otherwise have seen.
    /// Passive checks have the same shape: the reactivation period becomes downtime rather than
    /// a pause in rotation.</para>
    ///
    /// <para>So they switch themselves on the moment a cluster is given a second address, and
    /// stay out of the way until then. Scaling a service becomes an address list in
    /// configuration rather than a change here.</para>
    /// </summary>
    private static ClusterConfig Cluster(string clusterId, string addresses)
    {
        var destinations = addresses
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((address, i) => (Key: $"{clusterId}-{i}", Address: address))
            .ToDictionary(d => d.Key, d => new DestinationConfig { Address = d.Address });

        if (destinations.Count <= 1)
        {
            return new ClusterConfig { ClusterId = clusterId, Destinations = destinations };
        }

        return new ClusterConfig
        {
            ClusterId = clusterId,
            Destinations = destinations,
            // Two random destinations, the less loaded wins. Cheaper than LeastRequests at this
            // scale and markedly better than round-robin when one replica is degraded rather
            // than down -- which is the case health checks are worst at noticing.
            LoadBalancingPolicy = LoadBalancingPolicies.PowerOfTwoChoices,
            HealthCheck = new HealthCheckConfig
            {
                Active = new ActiveHealthCheckConfig
                {
                    Enabled = true,
                    Interval = TimeSpan.FromSeconds(10),
                    Timeout = TimeSpan.FromSeconds(5),
                    Policy = HealthCheckConstants.ActivePolicy.ConsecutiveFailures,
                    // The liveness probe, not the full one: a replica whose database is
                    // unreachable is not a replica to route around, because so is every other.
                    Path = "/health/live",
                },
                Passive = new PassiveHealthCheckConfig
                {
                    Enabled = true,
                    Policy = HealthCheckConstants.PassivePolicy.TransportFailureRate,
                    ReactivationPeriod = TimeSpan.FromSeconds(30),
                },
            },
            Metadata = new Dictionary<string, string>
            {
                [ConsecutiveFailuresHealthPolicyOptions.ThresholdMetadataName] = "3",
            },
            // Only for the hub-bearing cluster. SignalR's negotiate and the connection that
            // follows must reach the same replica: without a backplane, a WebSocket that lands
            // on a different one than negotiated is a connection that establishes and then
            // receives nothing, which presents as "chat is broken sometimes".
            SessionAffinity = clusterId == ContentApi
                ? new SessionAffinityConfig
                {
                    Enabled = true,
                    Policy = SessionAffinityConstants.Policies.Cookie,
                    FailurePolicy = SessionAffinityConstants.FailurePolicies.Redistribute,
                    AffinityKeyName = "dcms.affinity",
                }
                : null,
        };
    }
}
