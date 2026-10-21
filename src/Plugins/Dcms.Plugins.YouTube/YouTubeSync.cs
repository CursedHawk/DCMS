using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.PluginSdk.Runtime.Platform;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Cms;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.YouTube;

/// <param name="SourceProblem">The link itself is the trouble (no such channel, a private playlist): something an admin has to fix.</param>
public sealed record YouTubeSyncOutcome(int Created, int Updated, int Removed, string? Error, bool SourceProblem = false)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Syncs one instance: resolve the link, read the newest videos up to the cap, write them as
/// <c>youtube-video</c> items and drop the ones that are no longer among them.
///
/// <para>The same code serves the timer and the "Sync now" button, so the two cannot drift.
/// Quota: one unit for a timer pass that finds nothing new (the first playlist page); ≤4 for a
/// full sync (handle lookup cached a day; one playlist page per 50 videos, that first one
/// included; one videos read per 50; the Shorts page).
/// ponytail: one Google project gets 10,000 units a day; a quiet 30-minute pass costs one unit
/// (two for a playlist source), so a key serves about 100 instances. Past that, raise the quota
/// with Google or make the interval per instance.</para>
/// </summary>
public sealed class YouTubeSync(
    CmsDbContext cms,
    YouTubeClient youtube,
    ICacheService cache,
    ILogger<YouTubeSync> logger)
{
    private static readonly TimeSpan ChannelCacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan FeedCacheTtl = TimeSpan.FromDays(7);

    /// <param name="onlyWhenChanged">
    /// The timer's mode: read the playlist's first page (one unit) and stop there when it, the
    /// playlist's length and the settings are what the last sync saw. So the stored videos — titles,
    /// view counts — refresh when a new video arrives or someone presses Sync now, not every pass.
    /// </param>
    public async Task<YouTubeSyncOutcome> SyncAsync(
        PluginInstanceContext instance, CancellationToken ct, bool onlyWhenChanged = false)
    {
        if (!youtube.IsConfigured) return new(0, 0, 0, "YouTube is not configured on this deployment.");
        var settings = YouTubeSettings.Read(instance.Config);
        if (settings is null) return new(0, 0, 0, "Add a channel or playlist link in the settings first.", SourceProblem: true);

        using var rls = RlsScope.Tenant(instance.TenantId);
        try
        {
            var source = await ResolveAsync(settings, ct);
            if (source.Problem is { } problem) return new(0, 0, 0, problem, SourceProblem: true);

            var feedKey = $"youtube:feed:{instance.InstanceId:N}";
            var first = await FirstPageAsync(source.PlaylistId!, ct);
            var feed = Fingerprint(source.PlaylistId!, settings, first);
            if (onlyWhenChanged && await cache.GetAsync<string>(feedKey, ct) == feed) return new(0, 0, 0, null);

            var videos = await HarvestAsync(source.PlaylistId!, first, source.ChannelId, settings, ct);

            // A new video is news only when it is newer than every one already here: not on the
            // first import, and not when a raised cap backfills older ones.
            var newest = await cms.ContentItems.IgnoreQueryFilters()
                .Where(c => c.TenantId == instance.TenantId
                            && c.PluginInstanceId == instance.InstanceId
                            && c.ContentType == YouTubePlugin.VideoType)
                .MaxAsync(c => (DateTimeOffset?)c.CreatedAt, ct);

            var created = 0;
            var updated = 0;
            foreach (var video in videos)
            {
                var isNew = await SyncedContent.UpsertAsync(
                    cms, instance.TenantId, instance.InstanceId, YouTubePlugin.VideoType, video.Info.Id,
                    ToData(video), video.Info.PublishedAt, ct, keepHistory: false,
                    publish: settings.AutoPublish,
                    announce: newest is not null && video.Info.PublishedAt > newest);
                if (isNew) created++;
                else updated++;
            }

            var removed = await SyncedContent.RetainOnlyAsync(
                cms, instance.TenantId, instance.InstanceId, YouTubePlugin.VideoType,
                videos.Select(v => v.Info.Id).ToList(), ct);

            await cms.SaveChangesAsync(ct);
            await cache.SetAsync(feedKey, feed, FeedCacheTtl, ct);
            return new(created, updated, removed.Count, null);
        }
        catch (YouTubeApiException ex) when (ex.IsQuota)
        {
            cms.ChangeTracker.Clear();
            logger.LogWarning("YouTube quota exhausted syncing instance {InstanceId}.", instance.InstanceId);
            return new(0, 0, 0, "This deployment's daily YouTube quota is used up. The sync will try again later.");
        }
        catch (YouTubeApiException ex)
        {
            cms.ChangeTracker.Clear();
            logger.LogWarning(ex, "YouTube sync failed for instance {InstanceId}.", instance.InstanceId);
            return new(0, 0, 0, $"YouTube answered {(int)ex.Status}: {ex.Message}");
        }
    }

    /// <summary>What a link resolves to, for the settings screen's preview. Spends quota like a sync does.</summary>
    public async Task<(ChannelInfo? Channel, PlaylistInfo? Playlist, string? Problem)> PreviewAsync(
        string? channel, string? playlist, CancellationToken ct)
    {
        ChannelInfo? channelInfo = null;
        PlaylistInfo? playlistInfo = null;
        if (!string.IsNullOrWhiteSpace(channel))
        {
            var reference = YouTubeSource.ParseChannel(channel);
            if (reference is null) return (null, null, "That is not a YouTube channel link, @handle or channel id.");
            channelInfo = await ChannelAsync(reference, ct);
            if (channelInfo is null) return (null, null, $"No YouTube channel matches “{channel.Trim()}”.");
        }
        if (!string.IsNullOrWhiteSpace(playlist))
        {
            var id = YouTubeSource.ParsePlaylist(playlist);
            if (id is null) return (channelInfo, null, "That is not a YouTube playlist link or id.");
            playlistInfo = await youtube.PlaylistAsync(id, ct);
            if (playlistInfo is null) return (channelInfo, null, "That playlist does not exist or is not public.");
        }
        return (channelInfo, playlistInfo, null);
    }

    private sealed record Source(string? PlaylistId, string? ChannelId, string? Problem);

    private async Task<Source> ResolveAsync(YouTubeSettings settings, CancellationToken ct)
    {
        var (channel, playlist, problem) = await PreviewAsync(settings.Channel, settings.Playlist, ct);
        if (problem is not null) return new Source(null, null, problem);

        // A playlist wins over the uploads; its owner still names the Shorts to recognise.
        return playlist is not null
            ? new Source(playlist.Id, channel?.Id ?? playlist.ChannelId, null)
            : new Source(channel!.UploadsPlaylist, channel.Id, null);
    }

    private async Task<ChannelInfo?> ChannelAsync(ChannelRef reference, CancellationToken ct)
    {
        var key = $"youtube:channel:{reference.Kind}:{reference.Value.ToLowerInvariant()}";
        if (await cache.GetAsync<ChannelInfo>(key, ct) is { } cached) return cached;

        var found = await youtube.ChannelAsync(reference, ct);
        if (found is not null) await cache.SetAsync(key, found, ChannelCacheTtl, ct);
        return found;
    }

    private sealed record Page(IReadOnlyList<string> VideoIds, string? NextPageToken, int? Total);

    /// <summary>Null for a channel that has never uploaded: it has no uploads playlist yet.</summary>
    private async Task<Page?> FirstPageAsync(string playlistId, CancellationToken ct)
    {
        try
        {
            var (ids, next, total) = await youtube.PlaylistPageAsync(playlistId, null, ct);
            return new Page(ids, next, total);
        }
        catch (YouTubeApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// What the sync would see, in a hash: the settings, and the playlist's first page and length.
    /// A new upload changes the first page; a video added to or removed from a playlist, its length.
    /// </summary>
    private static string Fingerprint(string playlistId, YouTubeSettings settings, Page? first) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{settings}|{playlistId}|{first?.Total}:{string.Join(',', first?.VideoIds ?? [])}")));

    private sealed record Harvested(VideoInfo Info, string Kind);

    /// <summary>
    /// Pages the playlist until <see cref="YouTubeSettings.MaxVideos"/> videos pass the filters,
    /// with a hard page ceiling so a channel of nothing but Shorts cannot walk its whole history.
    /// </summary>
    private async Task<List<Harvested>> HarvestAsync(
        string playlistId, Page? first, string? channelId, YouTubeSettings settings, CancellationToken ct)
    {
        var kept = new List<Harvested>();
        if (first is null) return kept;

        var shorts = channelId is null ? [] : await ShortsAsync(channelId, ct);
        var maxPages = settings.MaxVideos / YouTubeClient.PageSize + 3;
        var (ids, pageToken) = (first.VideoIds, first.NextPageToken);

        for (var page = 0; page < maxPages && kept.Count < settings.MaxVideos; page++)
        {
            if (page > 0) (ids, pageToken, _) = await youtube.PlaylistPageAsync(playlistId, pageToken, ct);

            foreach (var video in await youtube.VideosAsync(ids, ct))
            {
                var kind = video.Live ? "live" : shorts.Contains(video.Id) ? "short" : "video";
                if (Keep(video, kind, settings) && kept.Count < settings.MaxVideos)
                {
                    kept.Add(new Harvested(video, kind));
                }
            }
            if (pageToken is null) break;
        }
        return kept;
    }

    public static bool Keep(VideoInfo video, string kind, YouTubeSettings settings) =>
        (kind != "short" || settings.IncludeShorts)
        && (kind != "live" || settings.IncludeLive)
        && (settings.SyncSince is not { } since
            || video.PublishedAt is not { } at
            || DateOnly.FromDateTime(at.UtcDateTime) >= since);

    /// <summary>The channel's recent Shorts (see <see cref="YouTubeSource.ShortsPlaylist"/>).</summary>
    private async Task<HashSet<string>> ShortsAsync(string channelId, CancellationToken ct)
    {
        try
        {
            var (ids, _, _) = await youtube.PlaylistPageAsync(YouTubeSource.ShortsPlaylist(channelId), null, ct);
            return [.. ids];
        }
        catch (YouTubeApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            // Normal for a channel with no Shorts; also what the undocumented playlist would
            // answer if Google retired it, in which case nothing is classed as a Short.
            logger.LogDebug("No Shorts playlist for channel {ChannelId}.", channelId);
            return [];
        }
    }

    private static JsonObject ToData(Harvested video)
    {
        var v = video.Info;
        return new JsonObject
        {
            ["videoId"] = v.Id,
            ["title"] = v.Title,
            ["description"] = v.Description,
            ["url"] = $"https://www.youtube.com/watch?v={v.Id}",
            ["embedUrl"] = $"https://www.youtube-nocookie.com/embed/{v.Id}",
            ["thumbnail"] = v.Thumbnail,
            ["publishedAt"] = v.PublishedAt?.ToString("O"),
            ["durationSeconds"] = v.DurationSeconds,
            ["viewCount"] = v.ViewCount,
            ["likeCount"] = v.LikeCount,
            ["kind"] = video.Kind,
            ["channelTitle"] = v.ChannelTitle,
        };
    }
}

/// <summary>
/// The scheduled pass: every enabled YouTube instance in the tenant, one after another. One
/// instance failing never stops the rest; a link that stopped working tells the admins once a day.
/// </summary>
public sealed class YouTubeSyncJob(
    YouTubeSync sync,
    PluginContextFactory instances,
    IConfiguration configuration,
    ILogger<YouTubeSyncJob> logger) : IPluginJobHandler
{
    public async Task RunAsync(JsonElement? payload, IPluginContext context, CancellationToken ct)
    {
        // Off in tests, which drive the sync through the Sync-now route instead of a timer.
        if (!configuration.GetValue("YouTube:SyncEnabled", true)) return;

        var own = (await instances.EnabledInstancesAsync(context.TenantId, ct))
            .Where(i => i.PluginId == YouTubePlugin.PluginId)
            .ToList();

        foreach (var instance in own)
        {
            YouTubeSyncOutcome outcome;
            try
            {
                outcome = await sync.SyncAsync(instance, ct, onlyWhenChanged: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "YouTube sync crashed for instance {InstanceId}.", instance.InstanceId);
                continue;
            }

            if (outcome.SourceProblem && YouTubeSettings.Read(instance.Config) is not null)
            {
                await context.Contracts.Get<IPluginNotifications>().RaiseAsync(new NotificationRaise(
                    Title: "YouTube videos are not updating",
                    Body: $"{instance.Name}: {outcome.Error}",
                    RequiredPermission: PlatformPermissions.PluginsManage,
                    // Once a day per instance: the pass runs every half hour and would otherwise
                    // repeat itself until someone fixes the link.
                    DedupeKey: $"youtube-source:{instance.InstanceId:N}:{DateTimeOffset.UtcNow:yyyyMMdd}",
                    Severity: NotificationSeverity.Warning,
                    LinkPath: $"/apps/{instance.Slug}/settings"), ct);
            }
            else if (!outcome.Ok)
            {
                logger.LogWarning("YouTube sync for instance {InstanceId}: {Error}", instance.InstanceId, outcome.Error);
            }
        }
    }
}
