using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Cms;
using Microsoft.EntityFrameworkCore;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// Serves published content for the delivery API. Reads through Redis: item keys
/// are exact, list keys fold in a per-instance generation counter so a single
/// counter bump invalidates every cached list for that instance (O(1)).
/// </summary>
public sealed class PublishedContentReader(CmsDbContext db, ICacheService cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public static string ItemKey(Guid tenantId, Guid instanceId, string type, string slug)
        => $"t:{tenantId}:c:{instanceId}:{type}:{slug}";

    public static string GenKey(Guid tenantId, Guid instanceId)
        => $"t:{tenantId}:c:{instanceId}:gen";

    public async Task<ContentItemDto?> GetBySlugAsync(
        Guid tenantId, Guid instanceId, string contentType, string slug, CancellationToken ct)
    {
        var key = ItemKey(tenantId, instanceId, contentType, slug);
        var cached = await cache.GetAsync<CachedItem>(key, ct);
        if (cached is not null)
        {
            return cached.Missing ? null : cached.ToDto();
        }

        var row = await QueryPublished(instanceId, contentType)
            .FirstOrDefaultAsync(x => x.Item.Slug == slug, ct);

        var item = row is null ? null : Map(row);
        await cache.SetAsync(key, CachedItem.From(item), Ttl, ct);
        return item;
    }

    /// <param name="onlyIds">
    /// When supplied, restricts the page to these items — how tag filtering is
    /// applied without a second path to published content. Not cached: the id set
    /// comes from a query that already ran, and folding an arbitrary list into the
    /// key would make one cache entry per tag combination.
    /// </param>
    public async Task<PagedResult<ContentItemDto>> ListAsync(
        Guid tenantId, Guid instanceId, string contentType, int page, int pageSize, CancellationToken ct,
        IReadOnlyCollection<Guid>? onlyIds = null)
    {
        var generation = await cache.GetAsync<long?>(GenKey(tenantId, instanceId), ct) ?? 0;
        var listKey = $"t:{tenantId}:c:{instanceId}:{contentType}:list:g{generation}:p{page}:s{pageSize}";
        var cacheable = onlyIds is null;

        var cached = cacheable ? await cache.GetAsync<CachedPage>(listKey, ct) : null;
        if (cached is not null)
        {
            return cached.ToResult();
        }

        var query = QueryPublished(instanceId, contentType);
        if (onlyIds is not null)
        {
            var ids = onlyIds as IList<Guid> ?? onlyIds.ToList();
            query = query.Where(x => ids.Contains(x.Item.Id));
        }
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(x => x.Item.PublishedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var result = new PagedResult<ContentItemDto>(rows.Select(Map).ToList(), page, pageSize, total);
        if (cacheable)
        {
            await cache.SetAsync(listKey, CachedPage.From(result), Ttl, ct);
        }
        return result;
    }

    private IQueryable<ItemWithVersion> QueryPublished(Guid instanceId, string contentType) =>
        from item in db.ContentItems.AsNoTracking()
        where item.PluginInstanceId == instanceId
              && item.ContentType == contentType
              && item.Status == ContentStatus.Published
              && item.PublishedVersionId != null
        join version in db.ContentVersions.AsNoTracking() on item.PublishedVersionId equals version.Id
        select new ItemWithVersion { Item = item, Version = version };

    private static ContentItemDto Map(ItemWithVersion row) => new(
        row.Item.Id,
        row.Item.PluginInstanceId,
        row.Item.ContentType,
        row.Item.Slug,
        row.Version.VersionNo,
        JsonDocument.Parse(row.Version.DataJson).RootElement,
        row.Item.PublishedAt ?? row.Item.UpdatedAt);

    private sealed class ItemWithVersion
    {
        public required ContentItem Item { get; init; }
        public required ContentVersion Version { get; init; }
    }

    // Cacheable projections (JsonElement isn't directly round-trippable through
    // the cache, so the payload is held as a raw JSON string).
    private sealed record CachedItem(bool Missing, Guid Id, Guid InstanceId, string Type, string Slug, int VersionNo, string DataJson, DateTimeOffset PublishedAt)
    {
        public static CachedItem From(ContentItemDto? dto) => dto is null
            ? new CachedItem(true, default, default, "", "", 0, "{}", default)
            : new CachedItem(false, dto.Id, dto.PluginInstanceId, dto.ContentType, dto.Slug, dto.VersionNo, dto.Data.GetRawText(), dto.PublishedAt);

        public ContentItemDto ToDto() => new(Id, InstanceId, Type, Slug, VersionNo, JsonDocument.Parse(DataJson).RootElement, PublishedAt);
    }

    private sealed record CachedPage(List<CachedItem> Items, int Page, int PageSize, long Total)
    {
        public static CachedPage From(PagedResult<ContentItemDto> r)
            => new(r.Items.Select(CachedItem.From).ToList()!, r.Page, r.PageSize, r.TotalCount);

        public PagedResult<ContentItemDto> ToResult()
            => new(Items.Select(i => i.ToDto()).ToList(), Page, PageSize, Total);
    }
}
