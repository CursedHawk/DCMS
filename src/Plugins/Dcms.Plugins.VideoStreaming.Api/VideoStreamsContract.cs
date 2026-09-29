using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.VideoStreaming.Api;

/// <summary>The fields of a published <c>stream</c>, as authored.</summary>
public sealed record VideoStream(string Title, Guid? Source, Guid? Poster);

[ContractEvent("video-streaming.stream.published")]
public sealed record VideoStreamPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("video-streaming.stream.unpublished")]
public sealed record VideoStreamUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Published streams of a Video streaming instance. Drafts never leave the plugin.</summary>
[DcmsContract("video-streaming.streams", 1, Description = "Published streams of a Video streaming instance.",
    Events = [typeof(VideoStreamPublished), typeof(VideoStreamUnpublished)])]
public interface IVideoStreams
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "Published streams, newest first.")]
    Task<ContentPage<VideoStream>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "One published stream by slug.")]
    Task<ContentEntry<VideoStream>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
