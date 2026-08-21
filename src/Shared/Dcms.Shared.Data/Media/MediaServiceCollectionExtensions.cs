using Dcms.Shared.Data.Audit;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Media;

public static class MediaServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsMediaData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<MediaDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", MediaDbContext.Schema))
                .UseDcmsAuditInterceptors(sp));

        return services;
    }

    /// <summary>
    /// Registers a no-op tenant context (TenantId null) for background services
    /// like media-worker, which scope tenancy explicitly per job via
    /// IgnoreQueryFilters rather than the ambient context.
    /// </summary>
    /// <remarks>
    /// Replaces rather than appends: AddDcmsTenancyData registers the Finbuckle
    /// bridge, which needs an IMultiTenantContextAccessor these hosts never set up.
    /// Appending would merely shadow it (last registration wins at resolution), and
    /// the unsatisfiable descriptor left behind fails ValidateOnBuild — so site-host
    /// could not start under Development at all.
    /// </remarks>
    public static IServiceCollection AddNullTenantContext(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Scoped<ITenantContext, NullTenantContext>());
        return services;
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}
