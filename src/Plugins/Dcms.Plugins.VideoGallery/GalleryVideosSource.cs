using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.VideoGallery.Api;

namespace Dcms.Plugins.VideoGallery;

internal sealed class GalleryVideosSource(IPluginContext context)
    : PublishedContentSource<GalleryVideo>(context, "video"), IGalleryVideos
{
    public new Task<ContentPage<GalleryVideo>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<GalleryVideo>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
