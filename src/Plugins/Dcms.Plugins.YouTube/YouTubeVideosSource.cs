using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.YouTube.Api;

namespace Dcms.Plugins.YouTube;

internal sealed class YouTubeVideosSource(IPluginContext context)
    : PublishedContentSource<YouTubeVideo>(context, YouTubePlugin.VideoType), IYouTubeVideos
{
    public new Task<ContentPage<YouTubeVideo>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<YouTubeVideo>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
