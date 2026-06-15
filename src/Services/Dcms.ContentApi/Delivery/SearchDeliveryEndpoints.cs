using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Search;
using Dcms.Shared.Kernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// Sitewide search delivery: GET /api/{slug}/search?q=... resolves the tenant's
/// enabled Search plugin instance and runs a Postgres full-text query
/// (websearch_to_tsquery) over the index, tenant-scoped via the ambient tenant.
/// </summary>
public static class SearchDeliveryEndpoints
{
    private const string SearchPluginId = "search";

    public static IEndpointRouteBuilder MapSearchDelivery(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/{slug}/search", async (
            string slug, string? q, int? limit, ITenantContext tenant,
            CmsDbContext cms, SearchDbContext search, CancellationToken ct) =>
        {
            if (tenant.TenantId is null)
            {
                return Results.NotFound();
            }
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.Ok(new { items = Array.Empty<object>(), total = 0 });
            }

            var instance = await cms.PluginInstances.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Slug == slug && p.PluginId == SearchPluginId && p.Enabled, ct);
            if (instance is null)
            {
                return Results.NotFound();
            }

            var take = Math.Clamp(limit ?? 20, 1, 50);
            var query = search.Documents.AsNoTracking()
                .Where(d => d.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("simple", q)));

            var total = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(d => d.SearchVector.Rank(EF.Functions.WebSearchToTsQuery("simple", q)))
                .Take(take)
                .Select(d => new { d.Title, d.Url, d.ContentType })
                .ToListAsync(ct);

            return Results.Ok(new { items, total });
        });

        return app;
    }
}
