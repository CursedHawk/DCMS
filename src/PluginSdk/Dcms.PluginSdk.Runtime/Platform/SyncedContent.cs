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
/// trims the oldest by that date or drops what the source no longer has. Nothing here saves: the caller saves the
/// <see cref="CmsDbContext"/> once per pass, outbox rows included.</para>
/// </summary>
// rls: every caller is a sync that has already entered RlsScope.Tenant for the instance's
// tenant, and every query here also names that tenant explicitly.
public static class SyncedContent
{
    /// <summary>
    /// Inserts or refreshes one item. Returns true when it was newly created.
    ///
    /// <para>An existing item's <c>Status</c> is deliberately left alone. That is the hide
    /// toggle: an admin who unpublishes a post is telling the site not to show it, and a sync
    /// that re-published it fifteen minutes later would make the control useless.</para>
    ///
    /// <para><paramref name="keepHistory"/> false rewrites the latest version in place instead of
    /// adding one — for a source whose items change on every pass (view counts), where a version
    /// per pass would grow the table without telling anyone anything. A version a person wrote
    /// (an admin's edit) is never rewritten: the sync adds its own after it.</para>
    ///
    /// <para>The draft pointer follows every write, as a manual publish leaves it: the console's
    /// editor shows the version <c>CurrentDraftVersionId</c> names, and an item without one opens
    /// as an empty form.</para>
    ///
    /// <para>Only a new item can be news. Refreshing one that is already live republishes it
    /// quietly (see <see cref="ContentPublished.Quiet"/>): a feed's counters change on every pass,
    /// and announcing each change notified the admins about every item, every pass.
    /// <paramref name="publish"/> false creates a new item as a draft for an admin to publish;
    /// <paramref name="announce"/> false publishes it without a notification.</para>
    /// </summary>
    public static async Task<bool> UpsertAsync(
        CmsDbContext cms, Guid tenantId, Guid instanceId, string contentType, string slug, JsonObject data,
        DateTimeOffset? postedAtUtc, CancellationToken ct, bool keepHistory = true, bool publish = true,
        bool announce = true)
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
            // Compared as JSON, not as text: jsonb hands the row back with its own key order and
            // spacing, so a string comparison saw a change on every pass and republished
            // everything, unchanged or not.
            if (latest is not null && JsonNode.DeepEquals(JsonNode.Parse(latest.DataJson), data))
            {
                // Items synced before the sync set the pointer get it on their next pass.
                existing.CurrentDraftVersionId ??= latest.Id;
                return false;
            }

            ContentVersion next;
            if (!keepHistory && latest is not null && latest.CreatedBy is null)
            {
                latest.DataJson = json;
                next = latest;
            }
            else
            {
                next = new ContentVersion
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    ItemId = existing.Id,
                    VersionNo = (latest?.VersionNo ?? 0) + 1,
                    DataJson = json,
                };
                cms.ContentVersions.Add(next);
            }

            existing.CurrentDraftVersionId = next.Id;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            // Only move the published pointer for an item that is actually published; leaving
            // an unpublished item alone is what makes the hide toggle stick.
            if (existing.Status == ContentStatus.Published)
            {
                existing.PublishedVersionId = next.Id;
                EnqueuePublished(cms, existing, quiet: true);
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
            Status = publish ? ContentStatus.Published : ContentStatus.Draft,
            CurrentDraftVersionId = versionId,
            PublishedVersionId = publish ? versionId : null,
            CreatedAt = postedAt,
            PublishedAt = publish ? postedAt : null,
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

        if (publish) EnqueuePublished(cms, item, quiet: !announce);
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

        return Remove(cms, stale);
    }

    /// <summary>
    /// Removes every item of one type whose slug is not in <paramref name="keep"/> — for a
    /// source that returns its whole current set each pass, so a post deleted at the source,
    /// or one a changed filter now excludes, leaves the site too. Returns the removed slugs.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RetainOnlyAsync(
        CmsDbContext cms, Guid tenantId, Guid instanceId, string contentType, IReadOnlyCollection<string> keep,
        CancellationToken ct)
    {
        var stale = await cms.ContentItems.IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                        && c.PluginInstanceId == instanceId
                        && c.ContentType == contentType
                        && !keep.Contains(c.Slug))
            .ToListAsync(ct);

        return Remove(cms, stale);
    }

    /// <summary>
    /// A removed item that was live gets an unpublish event, the same one a manual unpublish
    /// writes, so the delivery cache and the search index drop it rather than serving it on.
    /// A quiet one: an old post making room for a new one is not something to warn anyone about.
    /// </summary>
    private static List<string> Remove(CmsDbContext cms, List<ContentItem> stale)
    {
        foreach (var item in stale.Where(i => i.Status == ContentStatus.Published))
        {
            var evt = new ContentUnpublished(
                Guid.NewGuid(), DateTimeOffset.UtcNow, item.TenantId,
                item.PluginInstanceId, item.Id, item.ContentType, item.Slug, Quiet: true);
            cms.Outbox.Add(new ContentOutboxMessage
            {
                Id = Guid.NewGuid(),
                TenantId = item.TenantId,
                Subject = Subjects.ContentUnpublished,
                PayloadJson = JsonSerializer.Serialize(evt),
            });
        }
        cms.ContentItems.RemoveRange(stale);
        return stale.Select(s => s.Slug).ToList();
    }

    /// <summary>
    /// Writes the publish event to the same outbox a manual publish uses, so the Redis cache
    /// invalidation and the search index update come for free rather than being reimplemented.
    /// </summary>
    private static void EnqueuePublished(CmsDbContext cms, ContentItem item, bool quiet)
    {
        var evt = new ContentPublished(
            Guid.NewGuid(), DateTimeOffset.UtcNow, item.TenantId,
            item.PluginInstanceId, item.Id, item.ContentType, item.Slug, quiet);

        cms.Outbox.Add(new ContentOutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = item.TenantId,
            Subject = Subjects.ContentPublished,
            PayloadJson = JsonSerializer.Serialize(evt),
        });
    }
}
