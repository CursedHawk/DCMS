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
    /// <para>Removes every existing registration rather than appending or replacing one.
    /// <c>AddDcmsTenancyData</c> registers the Finbuckle bridge, which needs an
    /// <c>IMultiTenantContextAccessor</c> these hosts never set up. Appending would merely
    /// shadow it — last registration wins at resolution — and the unsatisfiable descriptor
    /// left behind fails <c>ValidateOnBuild</c>.</para>
    ///
    /// <para><c>Replace</c> is not enough either, which is the part that actually bit.
    /// It removes the <b>first</b> descriptor with the matching service type, and by the
    /// time a host calls this there are already two: <c>AddDcmsServiceDefaults</c> →
    /// <c>AddDcmsAudit</c> registers <c>TenantlessContext</c> as a fallback, then
    /// <c>AddDcmsTenancyData</c> adds the Finbuckle bridge. <c>Replace</c> dropped the
    /// fallback and left the bridge — so site-host could not start under Development at
    /// all, and in Production it worked only because <c>ValidateOnBuild</c> is off and the
    /// last registration happened to be this one. Reordering two lines in Program.cs would
    /// have turned that into a request-time failure.</para>
    ///
    /// <para>"Null tenant context" is an assertion about the host, not a preference among
    /// registrations, so it is expressed as one: remove all, add exactly one.</para>
    /// </remarks>
    public static IServiceCollection AddNullTenantContext(this IServiceCollection services)
    {
        services.RemoveAll<ITenantContext>();
        services.AddScoped<ITenantContext, NullTenantContext>();
        return services;
    }

    private sealed class NullTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public string? TenantSlug => null;
    }
}
