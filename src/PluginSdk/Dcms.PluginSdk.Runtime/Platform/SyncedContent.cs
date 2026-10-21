using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Microsoft.EntityFrameworkCore;

namespace Dcms.PluginSdk.Runtime.Platform;

/// <summary>
/// Writes an external feed (Instagram, Facebook, YouTube) into a plugin instance's content:
/// one item per external post, keyed by its external id as the slug, kept to a cap.
///
/// <para>Shared so the rules that are easy to get wrong live in one place: a sync never
/// republishes what an admin unpublished, dates items by when the source posted them, and
/// trims the oldest by that date. Nothing here saves: the caller saves the
/// <see cref="CmsDbContext"/> once per pass, outbox rows included.</para>
/// </summary>
public static class SyncedContent
{
    /// <summary>
    /// Inserts or refreshes one item. Returns true when it was newly created.
    ///
    /// <para>An existing item's <c>Status</c> is deliberately left alone. That is the hide
    /// toggle: an admin who unpublishes a post is telling the site not to show it, and a sync
    /// that re-published it fifteen minutes later would make the control useless.</para>
    /// </summary>
    public static async Task<bool> UpsertAsync(
        CmsDbContext cms, Guid tenantId, Guid instanceId, string contentType, string slug, JsonObject data,
        DateTimeOffset? postedAtUtc, CancellationToken ct)
    {
        var json = data.ToJsonString();

        var existing = await cms.ContentItems.IgnoreQueryFilters()
            .Include(c => c.Versions)
            .FirstOrDefaultAsync(c => c.TenantId == tenantId
                                      && c.PluginInstanceId == instanceId
                                      && c.ContentType == contentType
                                      && c.Slug == slug, ct);

        if (existing is not null)
        {
            var latest = existing.Versions.OrderByDescending(v => v.VersionNo).FirstOrDefault();
            if (latest is not null && latest.DataJson == json) return false;

            var next = new ContentVersion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ItemId = existing.Id,
                VersionNo = (latest?.VersionNo ?? 0) + 1,
                DataJson = json,
            };
            cms.ContentVersions.Add(next);

            existing.UpdatedAt = DateTimeOffset.UtcNow;
            // Only move the published pointer for an item that is actually published; leaving
            // an unpublished item alone is what makes the hide toggle stick.
            if (existing.Status == ContentStatus.Published)
            {
                existing.PublishedVersionId = next.Id;
                EnqueuePublished(cms, existing);
            }
            return false;
        }

        var itemId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        // The source's post date, not the moment we happened to fetch it. Both orderings that
        // matter read these columns -- delivery lists by PublishedAt, retention trims by
        // CreatedAt -- and stamping "now" gets both wrong the first time a cap is raised: the
        // backfilled older posts are inserted last, so they would sort newest and appear at the
        // top of the site while the genuinely recent ones got trimmed away underneath them.
        var postedAt = postedAtUtc ?? DateTimeOffset.UtcNow;

        var item = new ContentItem
        {
            Id = itemId,
            TenantId = tenantId,
            PluginInstanceId = instanceId,
            ContentType = contentType,
            Slug = slug,
            Status = ContentStatus.Published,
            PublishedVersionId = versionId,
            CreatedAt = postedAt,
            PublishedAt = postedAt,
        };
        cms.ContentItems.Add(item);
        cms.ContentVersions.Add(new ContentVersion
        {
            Id = versionId,
            TenantId = tenantId,
            ItemId = itemId,
            VersionNo = 1,
            DataJson = json,
        });

        EnqueuePublished(cms, item);
        return true;
    }

    /// <summary>
    /// Removes the items of one type beyond <paramref name="cap"/>, oldest first, and returns
    /// their slugs so the caller can release whatever it stored alongside them.
    /// </summary>
    public static async Task<IReadOnlyList<string>> TrimAsync(
        CmsDbContext cms, Guid tenantId, Guid instanceId, string contentType, int cap, CancellationToken ct)
    {
        var stale = await cms.ContentItems.IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                        && c.PluginInstanceId == instanceId
                        && c.ContentType == contentType)
            // CreatedAt, not PublishedAt: unpublishing an item nulls PublishedAt, and in
            // Postgres a NULL sorts first under DESC -- so ordering on it would park every
            // hidden item at the top of the keep list and trim live posts in their place.
            .OrderByDescending(c => c.CreatedAt)
            .Skip(cap)
            .ToListAsync(ct);

        cms.ContentItems.RemoveRange(stale);
        return stale.Select(s => s.Slug).ToList();
    }

    /// <summary>
    /// Writes the publish event to the same outbox a manual publish uses, so the Redis cache
    /// invalidation and the search index update come for free rather than being reimplemented.
    /// </summary>
    private static void EnqueuePublished(CmsDbContext cms, ContentItem item)
    {
        var evt = new ContentPublished(
            Guid.NewGuid(), DateTimeOffset.UtcNow, item.TenantId,
            item.PluginInstanceId, item.Id, item.ContentType, item.Slug);

        cms.Outbox.Add(new ContentOutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = item.TenantId,
            Subject = Subjects.ContentPublished,
            PayloadJson = JsonSerializer.Serialize(evt),
        });
    }
}
