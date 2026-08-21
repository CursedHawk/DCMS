using Dcms.Shared.Data.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Cms;

public static class CmsServiceCollectionExtensions
{
    /// <summary>
    /// Registers CmsDbContext (plugins + cms schemas). Requires an ITenantContext
    /// to be registered (e.g. via AddDcmsTenancyData).
    /// </summary>
    public static IServiceCollection AddDcmsCmsData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<CmsDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", CmsDbContext.CmsSchema))
                .UseDcmsAuditInterceptors(sp));

        return services;
    }
}
