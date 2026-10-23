using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Sites;

public static class SiteRoutingServiceCollectionExtensions
{
    /// <summary>
    /// Host → tenant site resolution (<see cref="DomainResolver"/>) with the invalidators that keep it
    /// current. Shared by site-host, which serves a site's files, and content-api, which answers its
    /// <c>/api</c> and <c>/hub</c> directly since 2026-10-23 -- one resolution and one invalidation path
    /// in both, never a second copy with its own idea of whose a hostname is (ADR 0010).
    /// </summary>
    public static IServiceCollection AddDcmsSiteRouting(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<DomainResolver>();
        services.AddHostedService<SiteCacheInvalidator>();
        services.AddHostedService<TenantStatusInvalidator>();
        return services;
    }
}
