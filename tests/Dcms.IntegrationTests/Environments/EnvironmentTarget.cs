using Dcms.Shared.Data;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// Opens the tenant's environments as the copy job does once it is done: for tests that copy
    /// into a new environment by hand, or work in it without waiting for the job.
    /// </summary>
    public static async Task OpenAsync(IServiceProvider services, string tenantSlug)
    {
        using var scope = services.CreateScope();
        using var rls = RlsScope.Platform();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var tenantId = Guid.Parse(await db.Tenants.IgnoreQueryFilters().Where(t => t.Identifier == tenantSlug).Select(t => t.Id).SingleAsync());
        await db.Environments.IgnoreQueryFilters().Where(e => e.TenantId == tenantId && e.ReadyAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.ReadyAt, DateTimeOffset.UtcNow));
        scope.ServiceProvider.GetRequiredService<EnvironmentStore>().Evict(tenantId);
    }
}
