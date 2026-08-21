using Dcms.Shared.Data.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Search;

public static class SearchServiceCollectionExtensions
{
    public static IServiceCollection AddDcmsSearchData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<SearchDbContext>((sp, options) =>
            options.UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", SearchDbContext.Schema))
                .UseDcmsAuditInterceptors(sp));

        return services;
    }
}
