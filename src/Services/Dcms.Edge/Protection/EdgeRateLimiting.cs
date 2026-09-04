using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Dcms.Edge.Protection;

/// <summary>
/// Rate limiting at the boundary, where the client address is a fact rather than a header.
///
/// <para><b>Why here and not only downstream.</b> Every service behind the edge already runs
/// <c>AddDcmsRateLimiting</c>, and each partitions on <c>RemoteIpAddress</c> — which, behind a
/// proxy, is whatever <c>UseForwardedHeaders</c> reconstructed from <c>X-Forwarded-For</c>. That
/// works, but it is a chain the edge assembles and every replica counts separately, so three
/// content-api replicas admit three times the configured limit. At the edge the remote address
/// is the actual TCP peer, there is one process counting, and a refused request costs no
/// downstream work at all.</para>
///
/// <para>The downstream limiters stay. They are what protects a service from something already
/// inside the compose network, and this one does not replace that.</para>
/// </summary>
public static class EdgeRateLimiting
{
    /// <summary>
    /// The sign-in surface: the token endpoint, the login form, password reset.
    ///
    /// <para>Its own policy because it is the one surface where the attack is cheap and the
    /// prize is an account. Everything else on this platform is rate-limited to keep a service
    /// standing up; this is limited to make credential stuffing slow.</para>
    /// </summary>
    public const string AuthPolicy = "edge.auth";

    public static void AddEdgeRateLimiting(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var permitLimit = configuration.GetValue("Edge:RateLimiting:PermitLimit", 1200);
        var windowSeconds = configuration.GetValue("Edge:RateLimiting:WindowSeconds", 60);
        var authPermitLimit = configuration.GetValue("Edge:RateLimiting:AuthPermitLimit", 30);
        var authWindowSeconds = configuration.GetValue("Edge:RateLimiting:AuthWindowSeconds", 60);

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Generous, and deliberately so: this is the ceiling that stops one address
            // saturating the platform, not a per-endpoint budget. A tenant site's page pulls
            // dozens of assets, and a limit tuned for API calls would break an ordinary visit.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                context => Partition(context, permitLimit, windowSeconds));

            options.AddPolicy(AuthPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authPermitLimit,
                    Window = TimeSpan.FromSeconds(authWindowSeconds),
                    QueueLimit = 0,
                }));

            options.OnRejected = (context, _) =>
            {
                // A Retry-After a client can act on, rather than a bare 429 that invites an
                // immediate retry -- which is how a limiter turns one impatient client into the
                // load it was meant to shed.
                context.HttpContext.Response.Headers.RetryAfter = windowSeconds.ToString();
                return ValueTask.CompletedTask;
            };
        });
    }

    private static RateLimitPartition<string> Partition(HttpContext context, int permitLimit, int windowSeconds)
    {
        var path = context.Request.Path;

        // Three exemptions, each of which would otherwise cause the failure it is meant to
        // prevent.
        //
        //   /.well-known/acme-challenge -- a certificate authority validating a domain is not a
        //   client to be throttled, and a 429 here fails issuance with an error that says
        //   nothing about rate limiting.
        //
        //   /health -- the compose probe and the deploy gate poll it every few seconds. A
        //   throttled probe reads as an unhealthy container, and the deploy rolls back an edge
        //   that is working.
        //
        //   WebSocket upgrades -- a long-lived connection is not what a fixed window measures,
        //   and a reconnect storm after a deploy would trip it for every client at once. This is
        //   the same exemption AddDcmsRateLimiting makes for /hub, expressed as what it actually
        //   means rather than as a path list the edge would have to keep in step.
        if (path.StartsWithSegments("/.well-known/acme-challenge")
            || path.StartsWithSegments("/health")
            || context.WebSockets.IsWebSocketRequest)
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                QueueLimit = 0,
            });
    }

    /// <summary>
    /// The connection's own remote address, and never a header.
    ///
    /// <para>The edge is the first hop, so this is the real peer. Partitioning on
    /// <c>X-Forwarded-For</c> here would let a client choose its own bucket by sending one —
    /// which is the whole reason <c>UseUntrustedHeaderScrubbing</c> drops it inbound, and it
    /// would be undone by reading it back a middleware later.</para>
    /// </summary>
    private static string ClientKey(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
