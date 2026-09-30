using System.Threading.RateLimiting;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Data.Cms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Meta.Core;

/// <summary>
/// The Meta machinery both feed plugins share — OAuth connections, the sync and token-refresh
/// workers, the stories read — registered and mapped <b>once</b> however many of the two are
/// installed. Each plugin calls these from its own <c>ConfigureServices</c> and
/// <c>MapHostEndpoints</c>; the first call does the work.
/// </summary>
public static class MetaSocial
{
    private sealed class Registered;

    private sealed class Mapped
    {
        public bool Done;
    }

    public static void AddServices(IServiceCollection services, PluginHost host)
    {
        if (services.Any(d => d.ServiceType == typeof(Registered)))
        {
            return;
        }
        services.AddSingleton<Registered>();
        services.AddSingleton<Mapped>();

        if (host.IsSite)
        {
            // Stories are read through the admin plane, which alone holds the Meta tokens. A
            // story fetch sits on a page request: better a missing story strip than a hung page.
            services.AddHttpClient(StoryDeliveryEndpoints.HttpClientName, (sp, client) =>
            {
                var baseUrl = sp.GetRequiredService<IConfiguration>()["Services:AdminApi"] ?? "http://localhost:5002";
                client.BaseAddress = new Uri(baseUrl);
                client.Timeout = TimeSpan.FromSeconds(10);
            });
            return;
        }

        // The OAuth callback is necessarily anonymous -- Meta redirects a browser to it with no
        // bearer token -- so it is the one unauthenticated write path on the admin plane. The
        // state token is 256 bits of randomness and guessing it is hopeless, but each guess
        // still costs a database lookup, so the endpoint gets its own named limiter.
        services.AddRateLimiter(limiter => limiter.AddPolicy(MetaOAuthEndpoints.CallbackRateLimitPolicy, http =>
            RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));

        // Absent credentials mean the feature simply does not offer itself -- same shape as
        // Google SSO in identity, so dev needs no Meta app.
        services.Configure<MetaSocialOptions>(host.Configuration.GetSection(MetaSocialOptions.SectionName));
        // Every OAuth URL is absolute (the two login paths live on different hosts).
        services.AddHttpClient<MetaOAuthClient>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<MetaGraphClient>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<MetaFeedFetcher>();
        services.AddScoped<MetaMediaMirror>();
        services.AddScoped<MetaFeedSyncService>();

        // Off in tests and anywhere without a Meta app: an enabled worker with no credentials
        // would just log failures every minute. The token refresh rides the same switch: a
        // long-lived token cannot be renewed once expired, so without it every connected
        // account stops about sixty days after it was connected.
        if (host.Configuration.GetValue("Social:SyncEnabled", true))
        {
            services.AddHostedService<MetaSyncWorker>();
            services.AddHostedService<MetaTokenRefreshWorker>();
        }
    }

    /// <summary>The admin plane's OAuth, connection and stories routes (their established URLs).</summary>
    public static void MapHostEndpoints(IEndpointRouteBuilder app, PluginHost host)
    {
        var mapped = app.ServiceProvider.GetRequiredService<Mapped>();
        if (mapped.Done || !host.IsAdmin)
        {
            return;
        }
        mapped.Done = true;
        MetaOAuthEndpoints.Map(app);
        MetaStoriesEndpoints.Map(app);
    }

    /// <summary>The feed plugin's <c>dcms.media@1</c>, for a sync started outside a plugin context (the sync-now button).</summary>
    public static async Task<IPluginMedia> MediaForAsync(IServiceProvider services, PluginInstance instance, CancellationToken ct)
    {
        var factory = services.GetRequiredService<PluginContextFactory>();
        var plugin = await factory.CreateAsync(instance.TenantId, instance.PluginId, null, PluginActor.System, ct);
        return plugin.Contracts.Get<IPluginMedia>();
    }
}
