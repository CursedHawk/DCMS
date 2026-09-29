using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.Carousel.Api;

namespace Dcms.Plugins.Carousel;

internal sealed class CarouselSlidesSource(IPluginContext context)
    : PublishedContentSource<CarouselSlide>(context, "slide"), ICarouselSlides
{
    public new Task<ContentPage<CarouselSlide>> ListAsync(ContentPageRequest input, CancellationToken ct) => base.ListAsync(input, ct);

    public new Task<ContentEntry<CarouselSlide>?> GetAsync(ContentSlugRequest input, CancellationToken ct) => base.GetAsync(input, ct);
}
