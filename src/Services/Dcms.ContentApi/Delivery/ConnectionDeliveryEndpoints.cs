using Dcms.Shared.Caching;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// A tenant's external API data (Mode D backlog #124): <c>GET /api/connections/{slug}{operation}</c>
/// answers with the last good response admin-api stored for exactly that allowed operation.
///
/// <para>Nothing here calls out or can: content-api holds no key that decrypts a connection's
/// credential (it is not granted <c>dcms-api-connections</c>), and an operation that was not
/// listed simply has no snapshot. So the public plane serves data, never access.</para>
/// </summary>
public static class ConnectionDeliveryEndpoints
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    public static IEndpointRouteBuilder MapConnectionDelivery(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/connections/{slug}/{**path}", async (
            string slug, string? path, HttpRequest request, ITenantContext tenant, CmsDbContext db, ICacheService cache,
            CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }
            var operation = "/" + (path ?? "") + request.QueryString.Value;
            var key = $"t:{tenantId}:connection:{slug}:{operation}";
            var body = await cache.GetAsync<string>(key, ct);
            if (body is null)
            {
                body = await db.ApiSnapshots
                    .Where(s => s.Operation == operation
                        && db.ApiConnections.Any(c => c.Id == s.ConnectionId && c.Slug == slug))
                    .Select(s => s.Body)
                    .FirstOrDefaultAsync(ct);
                if (body is null)
                {
                    return Results.NotFound();
                }
                await cache.SetAsync(key, body, Ttl, ct);
            }
            return Results.Content(body, "application/json");
        });
        return app;
    }
}
