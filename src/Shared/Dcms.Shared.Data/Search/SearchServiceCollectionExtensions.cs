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

        services.AddDcmsDbContext<SearchDbContext>(connectionString, SearchDbContext.Schema);

        return services;
    }
}
