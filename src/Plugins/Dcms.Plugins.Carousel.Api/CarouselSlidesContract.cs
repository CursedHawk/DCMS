using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Carousel.Api;

/// <summary>The fields of a published <c>slide</c>, as authored.</summary>
public sealed record CarouselSlide(string Title, string? Caption, Guid? Image, string? LinkUrl, double? Order);

[ContractEvent("carousel.slide.published")]
public sealed record CarouselSlidePublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("carousel.slide.unpublished")]
public sealed record CarouselSlideUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Published slides of a Carousel instance. Drafts never leave the plugin.</summary>
[DcmsContract("carousel.slides", 1, Description = "Published slides of a Carousel instance.",
    Events = [typeof(CarouselSlidePublished), typeof(CarouselSlideUnpublished)])]
public interface ICarouselSlides
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "Published slides, newest first.")]
    Task<ContentPage<CarouselSlide>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "One published slide by slug.")]
    Task<ContentEntry<CarouselSlide>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
