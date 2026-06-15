using Dcms.Shared.Kernel.Abstractions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Finbuckle.MultiTenant.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace Dcms.Shared.Data.Tenancy;

public static class TenancyServiceCollectionExtensions
{
    public const string TenantHeader = "X-Dcms-Tenant";

    /// <summary>
    /// Registers TenancyDbContext + the ITenantContext bridge. Call one of the
    /// resolution extensions to choose how the ambient tenant is determined.
    /// </summary>
    public static IServiceCollection AddDcmsTenancyData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? "Host=localhost;Port=5432;Database=dcms;Username=dcms;Password=dcms-dev";

        services.AddDbContext<TenancyDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", TenancyDbContext.Schema)));

        services.AddScoped<ITenantContext, FinbuckleTenantContext>();
        services.AddScoped<TenantStore>();
        return services;
    }

    /// <summary>Admin plane: tenant chosen explicitly by the SPA via header.</summary>
    public static IServiceCollection AddDcmsTenantResolutionByHeader(this IServiceCollection services)
    {
        services.AddMultiTenant<Tenant>()
            .WithHeaderStrategy(TenantHeader)
            .WithStore<TenantStore>(ServiceLifetime.Scoped);
        return services;
    }

    /// <summary>Delivery plane: tenant resolved from the request Host header.</summary>
    public static IServiceCollection AddDcmsTenantResolutionByHost(this IServiceCollection services)
    {
        services.AddMultiTenant<Tenant>()
            .WithHostStrategy("__tenant__.*")
            .WithStore<TenantStore>(ServiceLifetime.Scoped);
        return services;
    }
}
