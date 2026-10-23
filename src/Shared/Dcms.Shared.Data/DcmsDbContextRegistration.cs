using Dcms.Shared.Data.Audit;
using Dcms.Shared.Data.Rls;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data;

public static class DcmsDbContextRegistration
{
    /// <summary>
    /// Registers <typeparamref name="T"/> pooled (see <see cref="ScopedDbContext"/>): built once with the
    /// platform's interceptors, lent to each scope that asks for it, and returned to the pool when the
    /// scope ends. Requested exactly as before -- inject <typeparamref name="T"/>.
    ///
    /// <para>The interceptors come from the root container, so every one of them must be a singleton
    /// that reads what is per request through <see cref="ScopedDbContext.ServicesOf"/>.
    /// <paramref name="audited"/> false attaches only RLS enforcement: the audit store's own context,
    /// whose writes must not be audited again.</para>
    /// </summary>
    public static IServiceCollection AddDcmsDbContext<T>(
        this IServiceCollection services, string connectionString, string historySchema, bool audited = true)
        where T : ScopedDbContext
    {
        services.AddPooledDbContextFactory<T>((sp, options) =>
        {
            options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", historySchema));
            if (audited)
            {
                options.UseDcmsAuditInterceptors(sp);
            }
            else
            {
                options.UseDcmsRlsEnforcement(sp);
            }
        });
        services.AddScoped<T>(sp =>
        {
            var context = sp.GetRequiredService<IDbContextFactory<T>>().CreateDbContext();
            context.Bind(sp);
            return context;
        });
        return services;
    }
}
