using Dcms.Shared.Kernel.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Media;

public static class MediaServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsMediaData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<MediaDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", MediaDbContext.Schema)));

        return services;
    }

    /// <summary>
    /// Registers a no-op tenant context (TenantId null) for background services
    /// like media-worker, which scope tenancy explicitly per job via
    /// IgnoreQueryFilters rather than the ambient context.
    /// </summary>
    public static IServiceCollection AddNullTenantContext(this IServiceCollection services)
    {
        services.AddScoped<ITenantContext, NullTenantContext>();
        return services;
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}
