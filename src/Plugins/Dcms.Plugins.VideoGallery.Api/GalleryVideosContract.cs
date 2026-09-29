using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.VideoGallery.Api;

/// <summary>The fields of a published <c>video</c>, as authored.</summary>
public sealed record GalleryVideo(string Title, string? Description, Guid? Source);

[ContractEvent("video-gallery.video.published")]
public sealed record GalleryVideoPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("video-gallery.video.unpublished")]
public sealed record GalleryVideoUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Published videos of a Video gallery instance. Drafts never leave the plugin.</summary>
[DcmsContract("video-gallery.videos", 1, Description = "Published videos of a Video gallery instance.",
    Events = [typeof(GalleryVideoPublished), typeof(GalleryVideoUnpublished)])]
public interface IGalleryVideos
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "Published videos, newest first.")]
    Task<ContentPage<GalleryVideo>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "One published video by slug.")]
    Task<ContentEntry<GalleryVideo>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
