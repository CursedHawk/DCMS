using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Rls;

public static class RlsEnforcementServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TenantGucInterceptor"/> when <c>Rls:Enforce</c> is on, and nothing at
    /// all when it is off (ADR 0015).
    ///
    /// <para>Registered as <see cref="IInterceptor"/>, which is all it takes: every business
    /// context attaches every DI-registered interceptor through <c>UseDcmsAuditInterceptors</c>,
    /// so one registration reaches all of them.</para>
    ///
    /// <para>The flag belongs with the connection string. Setting it while a service still
    /// connects as the table owner sets a GUC the owner ignores; connecting as <c>dcms_app</c>
    /// without it leaves every tenant query empty. Phase 4 flips both, per service, together.</para>
    /// </summary>
    public static IServiceCollection AddDcmsRlsEnforcement(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue("Rls:Enforce", false))
        {
            return services;
        }

        // Registered as itself too, and as the same instance per scope: the interceptor keeps
        // per-connection bookkeeping, and a context that attaches it directly (see
        // UseDcmsRlsEnforcement) must share that with the ones that get it as an IInterceptor.
        services.AddScoped<TenantGucInterceptor>();
        services.AddScoped<IInterceptor>(sp => sp.GetRequiredService<TenantGucInterceptor>());
        return services;
    }

    /// <summary>
    /// Attaches <see cref="TenantGucInterceptor"/>, and only it, to a context that deliberately
    /// does not take every DI interceptor. <c>AuditDbContext</c> is the one that matters: it must
    /// not be audited (that would record the audit log's own writes), but <c>audit.audit_events</c>
    /// carries a tenant policy like any other table, so without this every chain append and every
    /// audit read under enforcement would run with no tenant at all. No-op when enforcement is off.
    /// </summary>
    public static DbContextOptionsBuilder UseDcmsRlsEnforcement(this DbContextOptionsBuilder options, IServiceProvider services)
        => services.GetService<TenantGucInterceptor>() is { } interceptor
            ? options.AddInterceptors(interceptor)
            : options;
}
