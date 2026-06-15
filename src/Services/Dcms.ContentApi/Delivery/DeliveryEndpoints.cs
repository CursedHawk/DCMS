using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// Generic content delivery: /api/{slug}/{contentType}[/{itemSlug}]. The tenant
/// is resolved from the request (host in prod, header in dev); the instance is
/// looked up by slug; the plugin's declared content routes gate what is served.
/// </summary>
public static class DeliveryEndpoints
{
    public static IEndpointRouteBuilder MapContentDelivery(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/{slug}/{contentType}", async (
            string slug, string contentType, int? page, int? pageSize,
            ITenantContext tenant, CmsDbContext db, PluginRouteTable routes,
            PublishedContentReader reader, CancellationToken ct) =>
        {
            var resolved = await ResolveAsync(slug, contentType, tenant, db, routes, ct);
            if (resolved is not { } r || !r.Route.List)
            {
                return Results.NotFound();
            }
            var size = Math.Clamp(pageSize ?? r.Route.Options.DefaultPageSize, 1, r.Route.Options.MaxPageSize);
            var result = await reader.ListAsync(r.TenantId, r.InstanceId, contentType, Math.Max(page ?? 1, 1), size, ct);
            return Results.Ok(result);
        });

        app.MapGet("/api/{slug}/{contentType}/{itemSlug}", async (
            string slug, string contentType, string itemSlug,
            ITenantContext tenant, CmsDbContext db, PluginRouteTable routes,
            PublishedContentReader reader, CancellationToken ct) =>
        {
            var resolved = await ResolveAsync(slug, contentType, tenant, db, routes, ct);
            if (resolved is not { } r || !r.Route.GetBySlug)
            {
                return Results.NotFound();
            }
            var item = await reader.GetBySlugAsync(r.TenantId, r.InstanceId, contentType, itemSlug, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        return app;
    }

    private static async Task<Resolved?> ResolveAsync(
        string slug, string contentType, ITenantContext tenant, CmsDbContext db, PluginRouteTable routes, CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return null;
        }
        var instance = await db.PluginInstances.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Slug == slug && p.Enabled, ct);
        if (instance is null)
        {
            return null;
        }
        var route = routes.Find(instance.PluginId, contentType);
        return route is null ? null : new Resolved(tenantId, instance.Id, route);
    }

    private readonly record struct Resolved(Guid TenantId, Guid InstanceId, ContentRouteInfo Route);
}
