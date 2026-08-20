using Dcms.Shared.Caching;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Kernel.Abstractions;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// The tenant's tag index: which tags its published content uses, and where.
///
/// Tags are the one piece of structure that crosses collections — the same
/// "live" can sit on an event, a gallery and a post — so a site that wants a
/// "everything tagged live" page has no endpoint to build it from. Per-collection
/// filtering (<c>?tag=</c> on a list) answers half of it; this answers the other
/// half, which is *what tags exist and which collections to ask*.
///
/// Published content only. This is the public delivery API: a draft's tags are
/// as unpublished as the draft.
/// </summary>
public static class TagDeliveryEndpoints
{
    /// <summary>How long a tag index is cached. Short: a publish changes it.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>Cached separately from the index, and read on every OpenAPI build.</summary>
    public static string AnyKey(Guid tenantId) => $"t:{tenantId}:tags:any";

    public static IEndpointRouteBuilder MapTagDelivery(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tags", async (
            string? contentType, string? field, ITenantContext tenant, CmsDbContext db,
            ICacheService cache, CancellationToken ct) =>
        {
            if (tenant.TenantId is not { } tenantId)
            {
                return Results.NotFound();
            }

            var key = $"t:{tenantId}:tags:index:{contentType ?? "*"}:{field ?? "*"}";
            var cached = await cache.GetAsync<List<TagEntry>>(key, ct);
            if (cached is null)
            {
                var rows = await TagQueries.OccurrencesAsync(
                    db, tenantId, publishedOnly: true, contentType: contentType, field: field, ct: ct);

                // Grouped case-insensitively but reported under the spelling used
                // most: a vocabulary that lists "Live" and "live" separately is not
                // a vocabulary, and picking the majority spelling is the one choice
                // that does not silently rename anyone's tag.
                cached = rows
                    .GroupBy(r => r.Tag.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new TagEntry(
                        g.OrderByDescending(r => r.Count).First().Tag.Trim(),
                        g.Sum(r => r.Count),
                        g.Select(r => new TagUse(
                                r.InstanceSlug,
                                r.ContentType,
                                r.Field,
                                r.Count,
                                $"/api/{Uri.EscapeDataString(r.InstanceSlug)}/{Uri.EscapeDataString(r.ContentType)}" +
                                $"?tag={Uri.EscapeDataString(r.Tag.Trim())}&tagField={Uri.EscapeDataString(r.Field)}"))
                            .OrderByDescending(u => u.Count)
                            .ToList()))
                    .OrderByDescending(t => t.Count)
                    .ThenBy(t => t.Tag, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                await cache.SetAsync(key, cached, Ttl, ct);
            }

            return Results.Ok(new { items = cached, totalCount = cached.Count });
        });

        return app;
    }

    /// <param name="Url">A ready-made delivery call for this tag in this collection.</param>
    public sealed record TagUse(string Instance, string ContentType, string Field, int Count, string Url);

    public sealed record TagEntry(string Tag, int Count, List<TagUse> Occurrences);

    /// <summary>
    /// Whether this tenant publishes any tags, cached briefly.
    ///
    /// Read on the OpenAPI path, which is why it is cached separately from the
    /// index: the document is itself cached, but the flag decides its cache key,
    /// so it is consulted even on a hit.
    /// </summary>
    public static async Task<bool> HasTagsAsync(
        Guid tenantId, CmsDbContext db, ICacheService cache, CancellationToken ct)
    {
        var cached = await cache.GetAsync<bool?>(AnyKey(tenantId), ct);
        if (cached is { } known)
        {
            return known;
        }
        var any = await TagQueries.AnyAsync(db, tenantId, publishedOnly: true, ct);
        await cache.SetAsync(AnyKey(tenantId), (bool?)any, Ttl, ct);
        return any;
    }
}
