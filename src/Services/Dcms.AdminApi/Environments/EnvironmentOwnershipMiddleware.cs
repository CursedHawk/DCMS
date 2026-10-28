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

        // rls: request tenant and environment -- every query below names the tenant, and the
        // environment filter keeps it to the request's environment (ADR 0028).
        var services = http.RequestServices;
        var tenancy = services.GetRequiredService<TenancyDbContext>();
        var environmentId = tenant.EnvironmentId ?? tenantId;
        var policies = await tenancy.EnvironmentPolicies.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.EnvironmentId == environmentId && p.Kind == managed.Kind)
            .ToListAsync(http.RequestAborted);
        if (policies.Count == 0)
        {
            await next(http);
            return;
        }
        // The item as it is, and as the request would name it: renaming an item of one's own onto
        // the name of one the pipeline owns, or creating one under that name, is refused as well.
        var key = await KeyAsync(managed.Kind, http, tenantId, services);
        var (renamed, ambiguous) = await BodyKeyAsync(managed.Kind, http);
        if (ambiguous)
        {
            // Which of the duplicates a handler binds is its serializer's business; checking one
            // and writing another would walk round the policy.
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "The request names the item more than once." }, http.RequestAborted);
            return;
        }
        if (EnvironmentMigrator.Ownership(policies, managed.Kind, key ?? "") != EnvironmentOwnership.Managed
            && (renamed is null || EnvironmentMigrator.Ownership(policies, managed.Kind, renamed) != EnvironmentOwnership.Managed))
        {
            await next(http);
            return;
        }

        var environments = await services.GetRequiredService<EnvironmentStore>().ListAsync(tenantId, http.RequestAborted);
        var sources = environments.FirstOrDefault(e => e.Id == environmentId)?.PromotesFrom ?? [];
        var names = environments.Where(e => sources.Contains(e.Id)).Select(e => e.Name).ToList();
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

    /// <summary>
    /// The key a request body gives the item (a role's new name, a new app's slug), when it can;
    /// ambiguous when the body names it more than once, in any casing.
    /// </summary>
    private static async Task<(string? Key, bool Ambiguous)> BodyKeyAsync(string kind, HttpContext http)
    {
        var property = kind switch
        {
            "roles" => "name",
            "plugins" => "slug",
            "user-auth" => "key",
            _ => null,
        };
        if (property is null || http.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true) return (null, false);
        http.Request.EnableBuffering();
        try
        {
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, false);
            var matches = doc.RootElement.EnumerateObject()
                .Where(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1) return (null, true);
            var value = matches.FirstOrDefault().Value;
            var text = value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString()?.Trim() : null;
            return (text is null ? null : kind == "user-auth" ? $"role:{text}" : text, false);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, false); // the handler answers a malformed body itself
        }
        finally
        {
            http.Request.Body.Position = 0;
        }
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
