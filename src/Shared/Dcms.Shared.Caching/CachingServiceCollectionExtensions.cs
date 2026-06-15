using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Dcms.Shared.Caching;

public static class CachingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Redis connection and cache service. Connection string
    /// comes from "ConnectionStrings:Redis" (default localhost:6379).
    /// </summary>
    public static IServiceCollection AddDcmsCaching(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Redis") ?? "localhost:6379";

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(connectionString + ",abortConnect=false"));
        services.AddSingleton<ICacheService, RedisCacheService>();

        services.AddHealthChecks().AddRedis(
            sp => sp.GetRequiredService<IConnectionMultiplexer>(),
            name: "redis",
            tags: ["ready"]);

        return services;
    }
}
