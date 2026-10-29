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
