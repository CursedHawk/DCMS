using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Dcms.Shared.Caching;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// An app link (ADR 0026): the consuming environment's <c>/api/{slug}</c> is served by the app of
/// that slug in another environment of its workspace — its published content, its Dynamic Apps
/// data. Done by naming the target as the request's tenant before Finbuckle reads it, so
/// everything after (the app's routes, its API access rules, its caches, its row rules) is simply
/// the target's, unchanged. A read link passes reads only; a write through it is refused rather
/// than quietly landing in the consumer's own app.
///
/// <para>Runs after <see cref="SiteHostResolution"/> has named the tenant, and only for a tenant
/// in a workspace with environments. The header saying which environment it came from is this
/// service's own: whatever a caller sent under that name is dropped first.</para>
/// </summary>
public static class EnvironmentLinkRouting
{
    public const string LinkedFromHeader = "X-Dcms-Linked-From";

    // Cleared by admin-api whenever a link of the environment changes; the expiry is a backstop.
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public static IApplicationBuilder UseEnvironmentLinks(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        request.Headers.Remove(LinkedFromHeader);
        if (request.Headers[TenancyServiceCollectionExtensions.TenantHeader].ToString() is not { Length: > 0 } slug
            || !request.Path.StartsWithSegments("/api", out var rest)
            || rest.Value?.TrimStart('/').Split('/')[0] is not { Length: > 0 } appSlug)
        {
            await next(context);
            return;
        }

        var services = context.RequestServices;
        var store = services.GetRequiredService<TenantStore>();
        var consumer = await store.GetByIdentifierAsync(slug);
        if (consumer?.WorkspaceId is null)
        {
            await next(context);
            return;
        }
        var link = await LinkAsync(services, consumer.TenantId, appSlug, context.RequestAborted);
        if (link is null)
        {
            await next(context);
            return;
        }
        var reading = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method);
        if (!reading && link.Access != EnvironmentLinkAccess.ReadWrite)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "This app is read from another environment and cannot be changed here." }, context.RequestAborted);
            return;
        }
        var target = await store.GetAsync(link.TargetTenantId.ToString());
        if (target is not null && target.WorkspaceId == consumer.WorkspaceId)
        {
            request.Headers[TenancyServiceCollectionExtensions.TenantHeader] = target.Identifier;
            request.Headers[LinkedFromHeader] = slug;
        }
        await next(context);
    });

    private static async Task<EnvironmentLinkEntry?> LinkAsync(IServiceProvider services, Guid tenantId, string appSlug, CancellationToken ct)
    {
        var cache = services.GetRequiredService<ICacheService>();
        var key = EnvironmentLinkKinds.CacheKey(tenantId);
        var links = await cache.GetAsync<EnvironmentLinkEntry[]>(key, ct);
        if (links is null)
        {
            using var rls = RlsScope.Tenant(tenantId);
            links = await services.GetRequiredService<TenancyDbContext>().EnvironmentLinks.IgnoreQueryFilters().AsNoTracking()
                .Where(l => l.TenantId == tenantId)
                .Select(l => new EnvironmentLinkEntry(l.Kind, l.TargetKey, l.TargetTenantId, l.Access))
                .ToArrayAsync(ct);
            await cache.SetAsync(key, links, Ttl, ct);
        }
        return links.FirstOrDefault(l => l.Kind == EnvironmentLinkKinds.App && l.TargetKey == appSlug);
    }
}
