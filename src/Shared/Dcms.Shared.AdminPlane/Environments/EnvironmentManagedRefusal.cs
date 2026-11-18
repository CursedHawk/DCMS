using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Environments;

/// <summary>
/// The 409 for a write to what an environment leaves to deployments (ADR 0026): shared by the
/// ownership middleware and plugin contract implementations that write outside a request route.
/// </summary>
public static class EnvironmentManagedRefusal
{
    /// <summary>
    /// For a handler whose write is managed only sometimes (a commit, when it lands on a site's
    /// release): the same 409 when this environment leaves <paramref name="key"/> to deployments,
    /// else null.
    /// </summary>
    public static async Task<IResult?> RefuseIfManagedAsync(
        IServiceProvider services, ITenantContext tenant, string kind, string key, CancellationToken ct)
    {
        if (!tenant.HasEnvironments || tenant.TenantId is not { } tenantId) return null;
        var environmentId = tenant.EnvironmentId ?? tenantId;
        // rls: request tenant and environment, as in InvokeAsync.
        var policies = await services.GetRequiredService<TenancyDbContext>().EnvironmentPolicies.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.EnvironmentId == environmentId && p.Kind == kind)
            .ToListAsync(ct);
        return EnvironmentMigrator.Ownership(policies, kind, key) == EnvironmentOwnership.Managed
            ? Refusal(await RefusalBodyAsync(services, tenantId, environmentId, kind, ct))
            : null;
    }

    public static IResult Refusal(object body) => Results.Json(body, statusCode: StatusCodes.Status409Conflict);

    public static async Task<object> RefusalBodyAsync(IServiceProvider services, Guid tenantId, Guid environmentId, string kind, CancellationToken ct)
    {
        var environments = await services.GetRequiredService<EnvironmentStore>().ListAsync(tenantId, ct);
        var sources = environments.FirstOrDefault(e => e.Id == environmentId)?.PromotesFrom ?? [];
        var names = environments.Where(e => sources.Contains(e.Id)).Select(e => e.Name).ToList();
        return new
        {
            error = names.Count > 0
                ? $"This is managed by deployments from {string.Join(" or ", names)}. Change it there and deploy it here."
                : "This is managed by deployments. Change it in the environment that deploys here.",
            code = "environment_managed",
            kind,
        };
    }
}
