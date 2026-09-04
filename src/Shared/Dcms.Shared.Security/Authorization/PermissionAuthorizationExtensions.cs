using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Security.Authorization;

public static class PermissionAuthorizationExtensions
{
    /// <summary>
    /// Registers the dynamic permission policy provider and the tenant permission handler.
    /// The caller must also register an <see cref="IPermissionResolver"/>.
    /// </summary>
    public static IServiceCollection AddDcmsPermissionAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        return services;
    }

    /// <summary>
    /// Registers the platform-console permission handler (and the shared policy provider, which
    /// understands both prefixes). The caller must also register an
    /// <see cref="IPlatformPermissionResolver"/>.
    /// </summary>
    /// <remarks>
    /// Safe to call alongside <see cref="AddDcmsPermissionAuthorization"/> in either order.
    /// Both register the same provider type, so last-wins resolution picks an equivalent
    /// instance either way, and handlers are additive.
    ///
    /// <para><c>AddSingleton</c> and not <c>TryAddSingleton</c>, in both methods: ASP.NET
    /// Core's own <c>AddAuthorization()</c> registers a <c>DefaultAuthorizationPolicyProvider</c>
    /// with <c>TryAddSingleton</c>, so a <c>TryAdd</c> here would be a no-op whenever
    /// <c>AddAuthorization()</c> ran first — and every permission policy would resolve to null,
    /// which an endpoint guarded only by that policy treats as allow.</para>
    /// </remarks>
    public static IServiceCollection AddDcmsPlatformPermissionAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PlatformPermissionAuthorizationHandler>();
        return services;
    }
}
