using Microsoft.AspNetCore.OutputCaching;

namespace Dcms.Edge.Protection;

/// <summary>
/// Caches published tenant pages at the edge, so a popular site stops costing site-host a
/// request per visitor.
///
/// <para><b>Off unless asked for</b> (<c>Edge:Cache:Enabled</c>). A cache on the delivery plane
/// is the kind of change that should be measured rather than assumed — the <c>loadtest/</c>
/// harness exists for exactly this comparison — and an unmeasured cache in front of every
/// tenant's site is a way to be confidently wrong in public.</para>
///
/// <para><b>Varying by host is not a detail.</b> The output cache keys on path and query by
/// default, and the edge serves every tenant from one process on one catch-all route. Without
/// <c>SetVaryByHost</c>, <c>GET /</c> for one tenant would be served to the next tenant who
/// asked for <c>GET /</c> — a cross-tenant content leak produced by a caching setting, on the
/// public internet. It is set here, and asserted by a test, because getting it wrong is silent:
/// the response is a valid page, just somebody else's.</para>
/// </summary>
public static class EdgeOutputCache
{
    /// <summary>The policy the tenant catch-all route names.</summary>
    public const string PublicPolicy = "edge.public";

    public static bool IsEnabled(IConfiguration configuration)
        => configuration.GetValue("Edge:Cache:Enabled", false);

    public static void AddEdgeOutputCache(this WebApplicationBuilder builder)
    {
        if (!IsEnabled(builder.Configuration))
        {
            return;
        }

        var seconds = builder.Configuration.GetValue("Edge:Cache:Seconds", 60);

        builder.Services.AddOutputCache(options =>
        {
            options.AddPolicy(PublicPolicy, policy => policy
                .Expire(TimeSpan.FromSeconds(seconds))
                // See the class remarks. This is the line between a cache and a leak.
                .SetVaryByHost(true)
                .AddPolicy<PublicPlaneCachePolicy>());
        });
    }

    /// <summary>
    /// Refuses to cache anything that could be about one visitor rather than about the site.
    ///
    /// <para>ASP.NET's default policy already declines non-GET, requests carrying
    /// <c>Authorization</c>, and responses carrying <c>Set-Cookie</c>. Two more are needed here,
    /// and both are the difference between a cache and a bug:</para>
    ///
    /// <list type="bullet">
    ///   <item><b>Any request with a cookie.</b> A visitor session, a form's anti-forgery token
    ///   or a chat identity makes the response about that person. The default policy does not
    ///   look at inbound cookies, only outbound ones, so a page rendered for a signed-in visitor
    ///   would be cached and then served to everyone.</item>
    ///   <item><b>Anything under <c>/api</c> or <c>/hub</c>.</b> Those are form submissions,
    ///   chat and visitor accounts proxied on to content-api. Caching a GET there is stale data
    ///   at best; the paths are excluded wholesale rather than trusting each endpoint's own
    ///   cache headers.</item>
    /// </list>
    /// </summary>
    private sealed class PublicPlaneCachePolicy : IOutputCachePolicy
    {
        public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
        {
            var request = context.HttpContext.Request;
            var personal = request.Headers.ContainsKey("Cookie")
                           || request.Path.StartsWithSegments("/api")
                           || request.Path.StartsWithSegments("/hub");

            if (personal)
            {
                context.EnableOutputCaching = false;
                context.AllowCacheLookup = false;
                context.AllowCacheStorage = false;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
        {
            // A response that sets a cookie is establishing something about this visitor, so it
            // is not a response to hand to the next one. The default policy checks this too;
            // stated again here because this policy replaces nothing and adds to everything, and
            // a reader should not have to know which half is inherited.
            if (context.HttpContext.Response.Headers.ContainsKey("Set-Cookie"))
            {
                context.AllowCacheStorage = false;
            }

            return ValueTask.CompletedTask;
        }
    }
}
