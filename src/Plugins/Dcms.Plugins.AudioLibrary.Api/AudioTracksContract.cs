using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.AudioLibrary.Api;

/// <summary>The fields of a published <c>track</c>, as authored.</summary>
public sealed record AudioTrack(string Title, string? Artist, Guid? Source);

[ContractEvent("audio-library.track.published")]
public sealed record AudioTrackPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("audio-library.track.unpublished")]
public sealed record AudioTrackUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Published tracks of an Audio library instance. Drafts never leave the plugin.</summary>
[DcmsContract("audio-library.tracks", 1, Description = "Published tracks of an Audio library instance.",
    Events = [typeof(AudioTrackPublished), typeof(AudioTrackUnpublished)])]
public interface IAudioTracks
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "Published tracks, newest first.")]
    Task<ContentPage<AudioTrack>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "One published track by slug.")]
    Task<ContentEntry<AudioTrack>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
