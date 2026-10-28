using Dcms.Shared.Data.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.IntegrationTests.Environments;

/// <summary>
/// Names a tenant, or one environment of it, the way the environment tests write it:
/// <c>slug</c> or <c>slug/environment</c> (ADR 0028).
/// </summary>
internal static class EnvironmentTarget
{
    public static void Apply(HttpRequestMessage req, string target)
    {
        var slash = target.IndexOf('/');
        req.Headers.Add("X-Dcms-Tenant", slash < 0 ? target : target[..slash]);
        if (slash >= 0) req.Headers.Add(EnvironmentResolution.Header, target[(slash + 1)..]);
    }

    /// <summary>The environment row, as the engine takes it.</summary>
    public static async Task<TenantEnvironment> LoadAsync(IServiceProvider services, Guid tenantId, Guid environmentId)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<EnvironmentStore>();
        store.Evict(tenantId);
        return await store.GetAsync(tenantId, environmentId, default)
               ?? throw new InvalidOperationException($"No environment {environmentId} in tenant {tenantId}.");
    }
}
