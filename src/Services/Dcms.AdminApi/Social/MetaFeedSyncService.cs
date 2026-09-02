using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.Facebook;
using Dcms.Plugins.Instagram;
using Dcms.Shared.Audit;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Social;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;

namespace Dcms.AdminApi.Social;

/// <summary>
/// Syncs one plugin instance: fetch within the caps, mirror the media, upsert the content,
/// then trim whatever the caps pushed out.
///
/// <para>Separate from the worker that schedules it so a "sync now" button and the timer run
/// exactly the same code — the alternative, where the manual path is a simplified copy, is how
/// the two quietly diverge until only one of them mirrors media.</para>
/// </summary>
public sealed class MetaFeedSyncService(
    SocialDbContext social,
    CmsDbContext cms,
    MediaDbContext media,
    MetaFeedFetcher fetcher,
    MetaMediaMirror mirror,
    ITransitEncryptor encryptor,
    IAuditRecorder audit,
    ILogger<MetaFeedSyncService> logger)
{
    public sealed record SyncOutcome(int Created, int Updated, int Trimmed, int PagesFetched, string? Error)
    {
        public bool Ok => Error is null;
    }

    public async Task<SyncOutcome> SyncInstanceAsync(PluginInstance instance, CancellationToken ct)
    {
        var settings = MetaFeedSettings.Read(instance.ConfigJson);
        if (settings is null)
        {
            // Installed but not connected. Normal, and not worth an error row.
            return new SyncOutcome(0, 0, 0, 0, null);
        }

        var connection = await social.Connections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == settings.ConnectionId && c.TenantId == instance.TenantId, ct);

        if (connection is null) return new SyncOutcome(0, 0, 0, 0, "The configured account is no longer connected.");
        if (connection.Status != MetaConnectionStatus.Active)
        {
            return new SyncOutcome(0, 0, 0, 0, $"The connected account needs attention ({connection.Status}).");
        }

        string accessToken;
        try
        {
            accessToken = await DecryptTokenAsync(connection, instance.PluginId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not decrypt the Meta token for connection {ConnectionId}.", connection.Id);
            return new SyncOutcome(0, 0, 0, 0, "The stored credential could not be read.");
        }

        MetaFeedFetcher.Harvest harvest;
        try
        {
            harvest = await fetcher.FetchAsync(connection, accessToken, instance.PluginId, settings, ct);
        }
        catch (MetaApiException ex)
        {
            if (ex.IsTokenInvalid)
            {
                // Not a transient failure. Backing off would hide a dead credential behind a
                // growing retry interval and the feed would simply stop with no explanation.
                connection.Status = MetaConnectionStatus.NeedsReauth;
                connection.LastError = "Meta rejected the stored credential. Reconnect the account.";
                await social.SaveChangesAsync(ct);
                return new SyncOutcome(0, 0, 0, 0, connection.LastError);
            }
            return new SyncOutcome(0, 0, 0, 0, $"Meta returned an error: {(int)ex.Status}.");
        }

        var created = 0;
        var updated = 0;

        foreach (var item in harvest.Items)
        {
            var contentType = MetaFeedFetcher.TypeOf(item, instance.PluginId);
            var data = await BuildDataAsync(instance, connection, item, settings, ct);

            if (await UpsertAsync(instance, contentType, item.ExternalId, data, item.PostedAt, ct)) created++;
            else updated++;
        }

        var trimmed = await TrimAsync(instance, settings, ct);

        await cms.SaveChangesAsync(ct);
        return new SyncOutcome(created, updated, trimmed, harvest.PagesFetched, null);
    }

    /// <summary>
    /// Decrypts the credential this feed needs, and records the access.
    ///
    /// <para>An Instagram account reached through a Facebook Page is read with the <i>Page's</i>
    /// token, not the user's — getting this wrong produces a permissions error that reads like
    /// a missing scope and sends you to App Review for a bug that is here.</para>
    /// </summary>
    private async Task<string> DecryptTokenAsync(MetaConnection connection, string pluginId, CancellationToken ct)
    {
        var usePageToken = connection.Provider == MetaProvider.Facebook
                           && connection.PageTokenCiphertext is { Length: > 0 };

        var ciphertext = usePageToken ? connection.PageTokenCiphertext! : connection.AccessTokenCiphertext;

        // Every decrypt of a tenant credential is recorded, the same way ai-gateway records
        // spending a tenant's provider key. A background job is exactly the context where an
        // unlogged credential read would be invisible.
        audit.Record(AuditActions.SecretAccessed)
            .For("meta_connection", connection.Id, connection.AccountName)
            .With("plugin", pluginId)
            .With("scope", usePageToken ? "page" : "user");

        var plaintext = await encryptor.DecryptAsync(
            VaultTransitServiceCollectionExtensions.SocialTokensKey, ciphertext, ct);

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>Builds the item payload, mirroring media unless the instance opted out.</summary>
    private async Task<JsonObject> BuildDataAsync(
        PluginInstance instance, MetaConnection connection, MetaFeedItem item,
        MetaFeedSettings settings, CancellationToken ct)
    {
        var isVideo = string.Equals(item.MediaType, "VIDEO", StringComparison.OrdinalIgnoreCase);
        var mirrorThis = !isVideo || settings.MirrorVideo;

        Guid? assetId = mirrorThis
            ? await mirror.MirrorAsync(instance.TenantId, connection.Id, item.ExternalId, item.MediaUrl, ct)
            : null;

        // A reel's poster frame is a small image and is mirrored even when the video is not,
        // so a feed with video mirroring off still renders as pictures rather than blanks.
        Guid? thumbnailId = item.ThumbnailUrl is { Length: > 0 }
            ? await mirror.MirrorAsync(instance.TenantId, connection.Id, $"{item.ExternalId}:thumb", item.ThumbnailUrl, ct)
            : null;

        var children = new JsonArray();
        foreach (var child in item.Children)
        {
            var childAsset = await mirror.MirrorAsync(
                instance.TenantId, connection.Id, $"{item.ExternalId}:{child.ExternalId}", child.MediaUrl, ct);

            children.Add(new JsonObject
            {
                ["externalId"] = child.ExternalId,
                ["mediaType"] = child.MediaType,
                ["media"] = childAsset?.ToString(),
                ["mediaUrl"] = childAsset is null ? child.MediaUrl : null,
            });
        }

        return new JsonObject
        {
            ["externalId"] = item.ExternalId,
            ["permalink"] = item.Permalink,
            ["caption"] = item.Caption,
            ["media"] = assetId?.ToString(),
            ["thumbnail"] = thumbnailId?.ToString(),
            ["mediaType"] = item.MediaType,
            // Only populated when we did not mirror. A site should prefer the stable asset;
            // this is the escape hatch for video the tenant chose not to transcode.
            ["mediaUrl"] = assetId is null ? item.MediaUrl : null,
            ["postedAt"] = item.PostedAt?.ToString("O"),
            ["username"] = item.Username ?? connection.AccountUsername,
            ["children"] = children,
        };
    }

    /// <summary>
    /// Inserts or refreshes one item. Returns true when it was newly created.
    ///
    /// <para>An existing item's <c>Status</c> is deliberately left alone. That is the hide
    /// toggle: an admin who unpublishes a post is telling the site not to show it, and a sync
    /// that re-published it fifteen minutes later would make the control useless.</para>
    /// </summary>
    private async Task<bool> UpsertAsync(
        PluginInstance instance, string contentType, string externalId, JsonObject data,
        DateTimeOffset? postedAtUtc, CancellationToken ct)
    {
        var json = data.ToJsonString();

        var existing = await cms.ContentItems.IgnoreQueryFilters()
            .Include(c => c.Versions)
            .FirstOrDefaultAsync(c => c.TenantId == instance.TenantId
                                      && c.PluginInstanceId == instance.Id
                                      && c.ContentType == contentType
                                      && c.Slug == externalId, ct);

        if (existing is not null)
        {
            var latest = existing.Versions.OrderByDescending(v => v.VersionNo).FirstOrDefault();
            if (latest is not null && latest.DataJson == json) return false;

            var next = new ContentVersion
            {
                Id = Guid.NewGuid(),
                TenantId = instance.TenantId,
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
                EnqueuePublished(instance, existing);
            }
            return false;
        }

        var itemId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        // Meta's post date, not the moment we happened to fetch it. Both orderings that matter
        // read these columns -- delivery lists by PublishedAt, retention trims by CreatedAt --
        // and stamping "now" gets both wrong the first time a cap is raised: the backfilled
        // older posts are inserted last, so they would sort newest and appear at the top of the
        // site while the genuinely recent ones got trimmed away underneath them.
        var postedAt = postedAtUtc ?? DateTimeOffset.UtcNow;

        var item = new ContentItem
        {
            Id = itemId,
            TenantId = instance.TenantId,
            PluginInstanceId = instance.Id,
            ContentType = contentType,
            Slug = externalId,
            Status = ContentStatus.Published,
            PublishedVersionId = versionId,
            CreatedAt = postedAt,
            PublishedAt = postedAt,
        };
        cms.ContentItems.Add(item);
        cms.ContentVersions.Add(new ContentVersion
        {
            Id = versionId,
            TenantId = instance.TenantId,
            ItemId = itemId,
            VersionNo = 1,
            DataJson = json,
        });

        EnqueuePublished(instance, item);
        return true;
    }

    /// <summary>
    /// Writes the publish event to the same outbox a manual publish uses, so the Redis cache
    /// invalidation and the search index update come for free rather than being reimplemented.
    /// </summary>
    private void EnqueuePublished(PluginInstance instance, ContentItem item)
    {
        var evt = new ContentPublished(
            Guid.NewGuid(), DateTimeOffset.UtcNow, instance.TenantId,
            instance.Id, item.Id, item.ContentType, item.Slug);

        cms.Outbox.Add(new ContentOutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = instance.TenantId,
            Subject = Subjects.ContentPublished,
            PayloadJson = JsonSerializer.Serialize(evt),
        });
    }

    /// <summary>
    /// Archives sync-owned items beyond the cap and releases the media they held.
    ///
    /// <para>Only assets recorded in <c>meta_media_map</c> are deleted. That table is the
    /// ownership record, and without it a retention sweep would be free to collect an admin's
    /// own uploads on the way past.</para>
    /// </summary>
    private async Task<int> TrimAsync(
        PluginInstance instance, MetaFeedSettings settings, CancellationToken ct)
    {
        var caps = instance.PluginId == InstagramPlugin.PluginId
            ? new Dictionary<string, int>
            {
                [InstagramPlugin.PostType] = settings.MaxPosts,
                [InstagramPlugin.ReelType] = settings.MaxReels,
            }
            : new Dictionary<string, int> { [FacebookPlugin.PostType] = settings.MaxPosts };

        var trimmed = 0;

        foreach (var (contentType, cap) in caps)
        {
            var items = await cms.ContentItems.IgnoreQueryFilters()
                .Where(c => c.TenantId == instance.TenantId
                            && c.PluginInstanceId == instance.Id
                            && c.ContentType == contentType)
                // CreatedAt, not PublishedAt: unpublishing an item nulls PublishedAt, and in
                // Postgres a NULL sorts first under DESC -- so ordering on it would park every
                // hidden item at the top of the keep list and trim live posts in their place.
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync(ct);

            foreach (var stale in items.Skip(cap))
            {
                cms.ContentItems.Remove(stale);
                trimmed++;

                var owned = await social.MediaMap.IgnoreQueryFilters()
                    .Where(m => m.TenantId == instance.TenantId
                                && m.ExternalMediaId.StartsWith(stale.Slug))
                    .ToListAsync(ct);

                foreach (var map in owned)
                {
                    var asset = await media.Assets.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(a => a.Id == map.MediaAssetId, ct);
                    if (asset is not null) media.Assets.Remove(asset);
                    social.MediaMap.Remove(map);
                }
            }
        }

        if (trimmed > 0)
        {
            await media.SaveChangesAsync(ct);
            await social.SaveChangesAsync(ct);
        }
        return trimmed;
    }
}
