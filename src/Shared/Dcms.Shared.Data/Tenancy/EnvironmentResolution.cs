using Dcms.Shared.Data.Rls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Shared.Data.Tenancy;

/// <summary>
/// A tenant's environments as requests resolve them (ADR 0028), cached briefly like
/// <see cref="TenantStore"/>: every request in a workspace with environments asks.
/// </summary>
public sealed class EnvironmentStore(TenancyDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<TenantEnvironment>> ListAsync(Guid tenantId, CancellationToken ct)
    {
        if (cache.TryGetValue(Key(tenantId), out IReadOnlyList<TenantEnvironment>? hit) && hit is not null)
        {
            return hit;
        }
        List<TenantEnvironment> environments;
        using (RlsScope.Tenant(tenantId))
        {
            environments = await db.Environments.IgnoreTenantFilter().AsNoTracking()
                .Where(e => e.TenantId == tenantId)
                .OrderBy(e => e.Order).ThenBy(e => e.Name)
                .ToListAsync(ct);
        }
        cache.Set(Key(tenantId), (IReadOnlyList<TenantEnvironment>)environments, Ttl);
        return environments;
    }

    public Task<TenantEnvironment?> FindAsync(Guid tenantId, string name, CancellationToken ct) =>
        FirstAsync(tenantId, e => string.Equals(e.Name, name, StringComparison.Ordinal), ct);

    public Task<TenantEnvironment?> GetAsync(Guid tenantId, Guid environmentId, CancellationToken ct) =>
        FirstAsync(tenantId, e => e.Id == environmentId, ct);

    // The cache is this process's: an environment another process just created is not in it, so
    // a miss reads again before it counts as none.
    private async Task<TenantEnvironment?> FirstAsync(Guid tenantId, Func<TenantEnvironment, bool> match, CancellationToken ct)
    {
        if ((await ListAsync(tenantId, ct)).FirstOrDefault(match) is { } hit) return hit;
        Evict(tenantId);
        return (await ListAsync(tenantId, ct)).FirstOrDefault(match);
    }

    /// <summary>The environment the request or message is in; null with no tenant.</summary>
    public async Task<TenantEnvironment?> CurrentAsync(Kernel.Abstractions.ITenantContext tenant, CancellationToken ct) =>
        tenant.TenantId is { } tenantId ? await GetAsync(tenantId, tenant.EnvironmentId ?? tenantId, ct) : null;

    /// <summary>Drops what this process cached, so a change it made applies to its next request.</summary>
    public void Evict(Guid tenantId) => cache.Remove(Key(tenantId));

    private static string Key(Guid tenantId) => $"tenant:environments:{tenantId}";
}

public static class EnvironmentResolution
{
    /// <summary>Names the request's environment; production when absent (ADR 0028).</summary>
    public const string Header = "X-Dcms-Environment";

    internal const string Item = "dcms.environment";
    private const string ManyItem = "dcms.environments.many";

    /// <summary>
    /// After the tenant is resolved: the environment <see cref="Header"/> names, or 404 for one the
    /// tenant does not have. Whether the caller may work there is the membership check's business.
    /// </summary>
    public static IApplicationBuilder UseDcmsEnvironment(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var name = context.Request.Headers[Header].ToString();
        var tenant = context.RequestServices.GetRequiredService<Kernel.Abstractions.ITenantContext>().TenantId;
        if (tenant is { } tenantId)
        {
            var store = context.RequestServices.GetRequiredService<EnvironmentStore>();
            var environment = name.Length > 0 ? await store.FindAsync(tenantId, name, context.RequestAborted) : null;
            context.Items[ManyItem] = (await store.ListAsync(tenantId, context.RequestAborted)).Count > 1;
            if (name.Length > 0 && environment is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = "This workspace has no environment of that name." }, context.RequestAborted);
                return;
            }
            if (environment is { IsReady: false })
            {
                // Still being copied: what anyone changed there now, the copy would write over. This
                // process's cache may predate the copy finishing on the worker, so ask once more.
                store.Evict(tenantId);
                environment = await store.FindAsync(tenantId, name, context.RequestAborted);
                if (environment is { IsReady: false })
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "This environment is still being set up. It opens once its copy is complete.",
                        code = "environment_setting_up",
                    }, context.RequestAborted);
                    return;
                }
            }
            if (environment is not null) context.Items[Item] = environment.Id;
        }
        if (tenant is { } current)
        {
            // Tenant blocks for this tenant inside the request stay in its environment.
            using var request = RlsScope.ForRequest(current, Of(context) ?? current);
            await next(context);
            return;
        }
        await next(context);
    });

    /// <summary>The environment the request resolved to, when it named one.</summary>
    public static Guid? Of(HttpContext? http) => http?.Items[Item] is Guid id ? id : null;

    /// <summary>The request's tenant has environments besides production.</summary>
    public static bool Many(HttpContext? http) => http?.Items[ManyItem] is true;
}
