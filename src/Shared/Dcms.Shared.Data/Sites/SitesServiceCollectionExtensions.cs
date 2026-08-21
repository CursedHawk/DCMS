using Dcms.Shared.Data.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Sites;

public static class SitesServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsSitesData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<SitesDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", SitesDbContext.Schema))
                .UseDcmsAuditInterceptors(sp));

        return services;
    }
}
