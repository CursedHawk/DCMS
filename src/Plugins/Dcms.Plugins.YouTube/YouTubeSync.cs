using System.Net;
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
/// Quota: ≤4 units a pass (handle lookup, cached a day; one playlist page per 50 videos; one
/// videos read per 50; the Shorts page).
/// ponytail: one Google project gets 10,000 units a day, so a fixed 30-minute pass serves about
/// 50 instances per key. Past that, raise the quota with Google or make the interval per instance.</para>
/// </summary>
public sealed class YouTubeSync(
    CmsDbContext cms,
    YouTubeClient youtube,
    ICacheService cache,
    ILogger<YouTubeSync> logger)
{
    private static readonly TimeSpan ChannelCacheTtl = TimeSpan.FromHours(24);

    public async Task<YouTubeSyncOutcome> SyncAsync(PluginInstanceContext instance, CancellationToken ct)
    {
        if (!youtube.IsConfigured) return new(0, 0, 0, "YouTube is not configured on this deployment.");
        var settings = YouTubeSettings.Read(instance.Config);
        if (settings is null) return new(0, 0, 0, "Add a channel or playlist link in the settings first.", SourceProblem: true);

        using var rls = RlsScope.Tenant(instance.TenantId);
        try
        {
            var source = await ResolveAsync(settings, ct);
            if (source.Problem is { } problem) return new(0, 0, 0, problem, SourceProblem: true);

            var videos = await HarvestAsync(source.PlaylistId!, source.ChannelId, settings, ct);

            var created = 0;
            var updated = 0;
            foreach (var video in videos)
            {
                var isNew = await SyncedContent.UpsertAsync(
                    cms, instance.TenantId, instance.InstanceId, YouTubePlugin.VideoType, video.Info.Id,
                    ToData(video), video.Info.PublishedAt, ct, keepHistory: false);
                if (isNew) created++;
                else updated++;
            }

            var removed = await SyncedContent.RetainOnlyAsync(
                cms, instance.TenantId, instance.InstanceId, YouTubePlugin.VideoType,
                videos.Select(v => v.Info.Id).ToList(), ct);

            await cms.SaveChangesAsync(ct);
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

    private sealed record Harvested(VideoInfo Info, string Kind);

    /// <summary>
    /// Pages the playlist until <see cref="YouTubeSettings.MaxVideos"/> videos pass the filters,
    /// with a hard page ceiling so a channel of nothing but Shorts cannot walk its whole history.
    /// </summary>
    private async Task<List<Harvested>> HarvestAsync(
        string playlistId, string? channelId, YouTubeSettings settings, CancellationToken ct)
    {
        var shorts = channelId is null ? [] : await ShortsAsync(channelId, ct);
        var maxPages = settings.MaxVideos / YouTubeClient.PageSize + 3;
        var kept = new List<Harvested>();
        string? pageToken = null;

        for (var page = 0; page < maxPages && kept.Count < settings.MaxVideos; page++)
        {
            IReadOnlyList<string> ids;
            try
            {
                (ids, pageToken) = await youtube.PlaylistPageAsync(playlistId, pageToken, ct);
            }
            catch (YouTubeApiException ex) when (ex.Status == HttpStatusCode.NotFound && page == 0)
            {
                // A channel that has never uploaded has no uploads playlist yet.
                break;
            }

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
            var (ids, _) = await youtube.PlaylistPageAsync(YouTubeSource.ShortsPlaylist(channelId), null, ct);
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
                outcome = await sync.SyncAsync(instance, ct);
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
