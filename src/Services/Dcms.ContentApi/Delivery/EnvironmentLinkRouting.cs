using Dcms.Shared.Data.Rls;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Dcms.Shared.Caching;

using Dcms.Shared.Data;
namespace Dcms.ContentApi.Delivery;

/// <summary>
/// An app link (ADR 0026): the consuming environment's <c>/api/{slug}</c> is served by the app of
/// that slug in another environment of its workspace — its published content, its Dynamic Apps
/// data. Done by naming the target as the request's environment (ADR 0028) before it is resolved,
/// so everything after (the app's routes, its API access rules, its caches, its row rules) is
/// simply the target's, unchanged. A read link passes reads only; a write through it is refused
/// rather than quietly landing in the consumer's own app.
///
/// <para>Runs after <see cref="SiteHostResolution"/> has named the tenant and environment, and only
/// for a tenant with environments. The header saying which environment it came from is this
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
        var tenant = await services.GetRequiredService<TenantStore>().GetByIdentifierAsync(slug);
        var all = tenant is null ? [] : await services.GetRequiredService<EnvironmentStore>().ListAsync(tenant.TenantId, context.RequestAborted);
        var name = request.Headers[EnvironmentResolution.Header].ToString();
        var consumer = all.Count < 2 ? null
            : name.Length == 0 ? all.FirstOrDefault(e => e.IsProduction)
            : all.FirstOrDefault(e => e.Name == name);
        if (consumer is null)
        {
            await next(context);
            return;
        }
        var link = await LinkAsync(services, consumer, appSlug, context.RequestAborted);
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
        if (all.FirstOrDefault(e => e.Id == link.TargetEnvironmentId) is { } target)
        {
            request.Headers[EnvironmentResolution.Header] = target.Name;
            request.Headers[LinkedFromHeader] = consumer.Name;
        }
        await next(context);
    });

    private static async Task<EnvironmentLinkEntry?> LinkAsync(IServiceProvider services, TenantEnvironment consumer, string appSlug, CancellationToken ct)
    {
        var cache = services.GetRequiredService<ICacheService>();
        var key = EnvironmentLinkKinds.CacheKey(consumer.Id);
        var links = await cache.GetAsync<EnvironmentLinkEntry[]>(key, ct);
        if (links is null)
        {
            using var rls = consumer.Scope();
            links = await services.GetRequiredService<TenancyDbContext>().EnvironmentLinks.IgnoreTenantFilter().AsNoTracking()
                .Where(l => l.TenantId == consumer.TenantId && l.EnvironmentId == consumer.Id)
                .Select(l => new EnvironmentLinkEntry(l.Kind, l.TargetKey, l.TargetEnvironmentId, l.Access, l.Grants))
                .ToArrayAsync(ct);
            await cache.SetAsync(key, links, Ttl, ct);
        }
        return links.FirstOrDefault(l => l.Kind == EnvironmentLinkKinds.App && l.TargetKey == appSlug);
    }
}
