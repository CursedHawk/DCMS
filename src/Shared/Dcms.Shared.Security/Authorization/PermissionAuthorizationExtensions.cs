using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Security.Authorization;

public static class PermissionAuthorizationExtensions
{
    /// <summary>
    /// Registers the dynamic permission policy provider and the handler. The
    /// caller must also register an <see cref="IPermissionResolver"/>.
    /// </summary>
    public static IServiceCollection AddDcmsPermissionAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        return services;
    }
}
