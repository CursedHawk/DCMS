using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Platform;
using System.Text;
using System.Text.Json.Nodes;
using Dcms.Shared.Audit;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Social;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;
using Dcms.Shared.Data.Rls;

using Dcms.Shared.Data;
namespace Dcms.Plugins.Meta.Core;

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

    /// <param name="media">The feed plugin's <c>dcms.media@1</c>, which the mirror imports through.</param>
    public async Task<SyncOutcome> SyncInstanceAsync(PluginInstance instance, IPluginMedia media, CancellationToken ct)
    {
        // One instance, one tenant, whether a timer or the sync-now button asked (ADR 0015). The
        // media lookup in TrimAsync names no tenant; this is what keeps it inside this one.
        using var rls = RlsScope.Tenant(instance.TenantId);
        var settings = MetaFeedSettings.Read(instance.ConfigJson);
        if (settings is null)
        {
            // Installed but not connected. Normal, and not worth an error row.
            return new SyncOutcome(0, 0, 0, 0, null);
        }

        var connection = await social.Connections.IgnoreTenantFilter()
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
            var data = await BuildDataAsync(media, instance, connection, item, settings, ct);

            if (await SyncedContent.UpsertAsync(
                    cms, instance.TenantId, instance.Id, contentType, item.ExternalId, data, item.PostedAt, ct)) created++;
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
        IPluginMedia media, PluginInstance instance, MetaConnection connection, MetaFeedItem item,
        MetaFeedSettings settings, CancellationToken ct)
    {
        var isVideo = string.Equals(item.MediaType, "VIDEO", StringComparison.OrdinalIgnoreCase);
        var mirrorThis = !isVideo || settings.MirrorVideo;

        Guid? assetId = mirrorThis
            ? await mirror.MirrorAsync(media, instance.TenantId, connection.Id, item.ExternalId, item.MediaUrl, ct)
            : null;

        // A reel's poster frame is a small image and is mirrored even when the video is not,
        // so a feed with video mirroring off still renders as pictures rather than blanks.
        Guid? thumbnailId = item.ThumbnailUrl is { Length: > 0 }
            ? await mirror.MirrorAsync(media, instance.TenantId, connection.Id, $"{item.ExternalId}:thumb", item.ThumbnailUrl, ct)
            : null;

        var children = new JsonArray();
        foreach (var child in item.Children)
        {
            var childAsset = await mirror.MirrorAsync(
                media, instance.TenantId, connection.Id, $"{item.ExternalId}:{child.ExternalId}", child.MediaUrl, ct);

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
    /// Archives sync-owned items beyond the cap and releases the media they held.
    ///
    /// <para>Only assets recorded in <c>meta_media_map</c> are deleted. That table is the
    /// ownership record, and without it a retention sweep would be free to collect an admin's
    /// own uploads on the way past.</para>
    /// </summary>
    private async Task<int> TrimAsync(
        PluginInstance instance, MetaFeedSettings settings, CancellationToken ct)
    {
        var caps = instance.PluginId == MetaPlugins.InstagramId
            ? new Dictionary<string, int>
            {
                [MetaPlugins.InstagramPost] = settings.MaxPosts,
                [MetaPlugins.InstagramReel] = settings.MaxReels,
            }
            : new Dictionary<string, int> { [MetaPlugins.FacebookPost] = settings.MaxPosts };

        var trimmed = 0;

        foreach (var (contentType, cap) in caps)
        {
            var stale = await SyncedContent.TrimAsync(cms, instance.TenantId, instance.Id, contentType, cap, ct);
            trimmed += stale.Count;

            foreach (var slug in stale)
            {
                var owned = await social.MediaMap.IgnoreTenantFilter()
                    .Where(m => m.TenantId == instance.TenantId
                                && m.ExternalMediaId.StartsWith(slug))
                    .ToListAsync(ct);

                foreach (var map in owned)
                {
                    var asset = await media.Assets.IgnoreTenantFilter()
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
