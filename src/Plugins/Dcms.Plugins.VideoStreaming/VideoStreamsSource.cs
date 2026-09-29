using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.VideoStreaming.Api;

namespace Dcms.Plugins.VideoStreaming;

internal sealed class VideoStreamsSource(IPluginContext context)
    : PublishedContentSource<VideoStream>(context, "stream"), IVideoStreams
{
    public new Task<ContentPage<VideoStream>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<VideoStream>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
