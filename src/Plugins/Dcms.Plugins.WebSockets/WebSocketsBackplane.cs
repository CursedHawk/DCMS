using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Dcms.Plugins.WebSockets;

/// <summary>
/// The hub's own Redis backplane (ADR 0037), on every plane: content-api serves the hub, admin-api
/// and jobs-worker send to it through <c>IHubContext&lt;WebSocketsHub&gt;</c>.
///
/// <para>A <b>closed</b> <c>HubLifetimeManager&lt;WebSocketsHub&gt;</c>, never
/// <c>AddStackExchangeRedis</c>: that one is process-wide (an open generic plus one shared
/// <c>RedisOptions</c>), so calling it here would move LiveChat's <c>dcms-chat</c> and the admin
/// plane's <c>dcms-notify</c> onto whichever prefix registered last. DI prefers a closed
/// registration over the open generic, so this hub alone uses <see cref="ChannelPrefix"/>.</para>
/// </summary>
public static class WebSocketsBackplane
{
    public const string ChannelPrefix = "dcms-ws";

    public static void Register(IServiceCollection services, IConfiguration configuration)
    {
        // The core services (IHubContext, protocols); idempotent beside the host's own call.
        services.AddSignalR();
        var redis = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redis))
        {
            // One process (local dev, tests): the default in-memory manager is the whole fan-out.
            return;
        }
        services.AddSingleton<HubLifetimeManager<WebSocketsHub>>(sp =>
        {
            var redisOptions = new RedisOptions { Configuration = ConfigurationOptions.Parse(redis + ",abortConnect=false") };
            redisOptions.Configuration.ChannelPrefix = RedisChannel.Literal(ChannelPrefix);
            return new RedisHubLifetimeManager<WebSocketsHub>(
                sp.GetRequiredService<ILogger<RedisHubLifetimeManager<WebSocketsHub>>>(),
                Options.Create(redisOptions),
                sp.GetRequiredService<IHubProtocolResolver>(),
                sp.GetService<IOptions<HubOptions>>(),
                sp.GetService<IOptions<HubOptions<WebSocketsHub>>>());
        });
    }
}
