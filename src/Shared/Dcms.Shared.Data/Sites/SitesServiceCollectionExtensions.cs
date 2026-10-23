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

        services.AddDcmsDbContext<SitesDbContext>(connectionString, SitesDbContext.Schema);

        return services;
    }
}
