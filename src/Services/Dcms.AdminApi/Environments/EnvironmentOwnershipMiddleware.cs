using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Data.UserAuth;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Environments;

/// <summary>
/// Refuses a direct change to configuration an environment leaves to deployments (ADR 0026):
/// a write to a route marked <see cref="EnvironmentManagedMetadata"/> whose kind — or the one item
/// it touches — is <see cref="EnvironmentOwnership.Managed"/> here answers
/// <c>409 environment_managed</c>. Deployments write through the kinds in a job, not through these
/// routes, so they are never stopped by it.
///
/// <para>The item is named the way the kind exports it; a route that creates one has no item yet
/// and answers to the kind's policy alone.</para>
/// </summary>
public sealed class EnvironmentOwnershipMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, ITenantContext tenant)
    {
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
            || !tenant.HasEnvironments || tenant.TenantId is not { } tenantId
            || http.GetEndpoint()?.Metadata.GetMetadata<EnvironmentManagedMetadata>() is not { } managed)
        {
            await next(http);
            return;
        }

        // rls: request tenant -- every query below names it, and Finbuckle resolved it.
        var services = http.RequestServices;
        var tenancy = services.GetRequiredService<TenancyDbContext>();
        var policies = await tenancy.EnvironmentPolicies.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Kind == managed.Kind)
            .ToListAsync(http.RequestAborted);
        if (policies.Count == 0)
        {
            await next(http);
            return;
        }
        var key = await KeyAsync(managed.Kind, http, tenantId, services);
        if (EnvironmentMigrator.Ownership(policies, managed.Kind, key ?? "") != EnvironmentOwnership.Managed)
        {
            await next(http);
            return;
        }

        var sources = await tenancy.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId.ToString()).Select(t => t.PromotesFrom).FirstOrDefaultAsync(http.RequestAborted) ?? [];
        var names = await tenancy.Tenants.AsNoTracking()
            .Where(t => sources.Select(s => s.ToString()).Contains(t.Id)).Select(t => t.EnvironmentName).ToListAsync(http.RequestAborted);
        http.Response.StatusCode = StatusCodes.Status409Conflict;
        await http.Response.WriteAsJsonAsync(new
        {
            error = names.Count > 0
                ? $"This is managed by deployments from {string.Join(" or ", names)}. Change it there and deploy it here."
                : "This is managed by deployments. Change it in the environment that deploys here.",
            code = "environment_managed",
            kind = managed.Kind,
        }, http.RequestAborted);
    }

    /// <summary>The item a route touches, keyed as its kind exports it; null for a new one.</summary>
    private static async Task<string?> KeyAsync(string kind, HttpContext http, Guid tenantId, IServiceProvider services)
    {
        var values = http.Request.RouteValues;
        Guid? Id(string name) => Guid.TryParse(values[name]?.ToString(), out var id) ? id : null;
        var ct = http.RequestAborted;
        switch (kind)
        {
            case "roles" when Id("id") is { } roleId:
                return await services.GetRequiredService<TenancyDbContext>().TenantRoles.AsNoTracking()
                    .Where(r => r.TenantId == tenantId && r.Id == roleId).Select(r => r.Name).FirstOrDefaultAsync(ct);
            case "plugins" when Id("id") is { } instanceId:
                return await services.GetRequiredService<CmsDbContext>().PluginInstances.AsNoTracking()
                    .Where(p => p.TenantId == tenantId && p.Id == instanceId).Select(p => p.Slug).FirstOrDefaultAsync(ct);
            case "api-connections" or "dynamic-apps":
                return values["slug"]?.ToString();
            case "sites" when Id("id") is { } siteId:
                var site = await services.GetRequiredService<SitesDbContext>().Sites.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Id == siteId, ct);
                return site?.KeyAcrossEnvironments;
            case "user-auth" when Id("roleId") is { } userRoleId:
                var roleKey = await services.GetRequiredService<UserAuthDbContext>().Roles.AsNoTracking()
                    .Where(r => r.TenantId == tenantId && r.Id == userRoleId).Select(r => r.Key).FirstOrDefaultAsync(ct);
                return roleKey is null ? null : $"role:{roleKey}";
            case "user-auth" when Id("instanceId") is { } appId:
                var slug = await services.GetRequiredService<CmsDbContext>().PluginInstances.AsNoTracking()
                    .Where(p => p.TenantId == tenantId && p.Id == appId).Select(p => p.Slug).FirstOrDefaultAsync(ct);
                return slug is null ? null : $"api:{slug}";
            default:
                return null;
        }
    }
}
