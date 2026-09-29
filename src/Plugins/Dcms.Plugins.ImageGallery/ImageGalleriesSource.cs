using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.ImageGallery.Api;

namespace Dcms.Plugins.ImageGallery;

internal sealed class ImageGalleriesSource(IPluginContext context)
    : PublishedContentSource<Gallery>(context, "gallery"), IImageGalleries
{
    public new Task<ContentPage<Gallery>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<Gallery>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
