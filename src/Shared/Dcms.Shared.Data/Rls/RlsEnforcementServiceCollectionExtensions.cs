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

        services.AddScoped<IInterceptor, TenantGucInterceptor>();
        return services;
    }
}
