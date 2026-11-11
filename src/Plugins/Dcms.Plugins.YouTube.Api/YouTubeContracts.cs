using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.YouTube.Api;

/// <summary>
/// A video as the sync stores it. <c>EmbedUrl</c> is the privacy-enhanced player address, ready
/// for an iframe; <c>Thumbnail</c> is YouTube's own image URL. <c>Kind</c> is <c>video</c>,
/// <c>short</c> or <c>live</c> (a live stream, premiere or its recording).
/// </summary>
public sealed record YouTubeVideo(
    string? VideoId,
    string? Title,
    string? Description,
    string? Url,
    string? EmbedUrl,
    string? Thumbnail,
    string? PublishedAt,
    int? DurationSeconds,
    long? ViewCount,
    long? LikeCount,
    string? Kind,
    string? ChannelTitle);

[ContractEvent("youtube.video.published")]
public sealed record YouTubeVideoPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("youtube.video.unpublished")]
public sealed record YouTubeVideoUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Videos synced from a YouTube channel or playlist.</summary>
[DcmsContract("youtube.videos", 1, Description = "Videos synced from a YouTube channel or playlist.", Events = [typeof(YouTubeVideoPublished), typeof(YouTubeVideoUnpublished)])]
public interface IYouTubeVideos
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai | OpExposure.Link,
        ReturnsExternalText = true, Description = "Synced videos, newest first.")]
    Task<ContentPage<YouTubeVideo>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai | OpExposure.Link,
        ReturnsExternalText = true, Description = "One synced video by its YouTube id.")]
    Task<ContentEntry<YouTubeVideo>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}

/// <param name="Channel">A channel link, handle (<c>@name</c>) or id; or give <paramref name="Playlist"/>.</param>
public sealed record YouTubeSourceQuery(string? Channel = null, string? Playlist = null);

/// <param name="Problem">Why the link does not resolve, when it does not.</param>
public sealed record YouTubeSourcePreview(string? ChannelId, string? ChannelTitle, string? PlaylistId, string? PlaylistTitle, string? Problem);

public sealed record YouTubeSyncResult(int Created, int Updated, int Removed);

/// <summary>Checking a channel link and syncing now, as the instance's settings screen does — for the assistant.</summary>
[DcmsContract("youtube.sync", 1, Description = "Check what a YouTube channel or playlist link resolves to, and sync an instance's videos now.")]
public interface IYouTubeSyncing
{
    [Operation(OpRisk.Read, Permission = "plugin:youtube:sync", Expose = OpExposure.Admin | OpExposure.Ai, ReturnsExternalText = true,
        Description = "What a channel or playlist link resolves to, before it is saved in the instance's settings.")]
    Task<YouTubeSourcePreview> ResolveAsync(YouTubeSourceQuery input, CancellationToken ct);

    [Operation(OpRisk.Safe, Permission = "plugin:youtube:sync", Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Pull the instance's latest videos now rather than at the next scheduled pass.")]
    Task<YouTubeSyncResult> SyncNowAsync(CancellationToken ct);
}
