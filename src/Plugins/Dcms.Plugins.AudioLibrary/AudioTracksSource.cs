using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.AudioLibrary.Api;

namespace Dcms.Plugins.AudioLibrary;

internal sealed class AudioTracksSource(IPluginContext context)
    : PublishedContentSource<AudioTrack>(context, "track"), IAudioTracks
{
    public new Task<ContentPage<AudioTrack>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<AudioTrack>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
