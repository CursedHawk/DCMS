using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Sites;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// A tenant domain's <c>/api</c> and <c>/hub</c> come from the edge straight to content-api (its
/// <c>tenant-api</c> and <c>tenant-hub</c> routes), carrying only the visitor's Host. This resolves
/// that Host to its tenant and site and states them as the headers the rest of content-api reads --
/// the tenant for Finbuckle, the site for analytics -- exactly as site-host's API proxy used to before
/// forwarding. Until 2026-10-23 every such request took that extra hop: ~2 ms of CPU and two spans.
///
/// <para>Resolved here and not at the edge (ADR 0010): the lookup is <see cref="DomainResolver"/>,
/// shared with site-host along with its invalidators, so there is one answer to whose a hostname is,
/// in services that already read tenancy. A host it refuses -- unverified, unlinked, unpublished,
/// suspended -- is a 404, as it was at site-host.</para>
///
/// <para>Only a request with no tenant header and a dotted Host: the operator plane (the admin host's
/// hub) and site-host still name the tenant themselves, and internal callers address the service by
/// its undotted compose name. The edge drops a visitor's own X-Dcms-Tenant and X-Dcms-Site* on these
/// routes, so what this sets cannot be forged from outside.</para>
/// </summary>
public static class SiteHostResolution
{
    private const string SiteHeader = "X-Dcms-Site";
    private const string SiteHostHeader = "X-Dcms-Site-Host";

    public static IApplicationBuilder UseSiteHostResolution(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        var host = request.Host.Host;
        if (request.Headers.ContainsKey(TenancyServiceCollectionExtensions.TenantHeader)
            || !host.Contains('.', StringComparison.Ordinal)
            || !(request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/hub")))
        {
            await next(context);
            return;
        }

        var route = await context.RequestServices.GetRequiredService<DomainResolver>().ResolveAsync(host, context.RequestAborted);
        if (route is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        request.Headers[TenancyServiceCollectionExtensions.TenantHeader] = route.TenantSlug;
        request.Headers[SiteHeader] = route.SiteId.ToString();
        request.Headers[SiteHostHeader] = host.ToLowerInvariant();
        await next(context);
    });
}
