using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Edge;

public static class EdgeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the edge's certificate, ACME-account and route tables.
    ///
    /// <para>No tenant context is registered alongside: every read here is by hostname, from a
    /// connection that has no ambient tenant and never will — the caller is a TLS handshake, not
    /// a signed-in user.</para>
    /// </summary>
    public static IServiceCollection AddDcmsEdgeData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<EdgeDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EdgeDbContext.Schema)));

        return services;
    }
}
