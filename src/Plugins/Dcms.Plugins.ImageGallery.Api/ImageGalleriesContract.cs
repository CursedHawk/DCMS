using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.ImageGallery.Api;

/// <summary>The fields of a published <c>gallery</c>, as authored.</summary>
public sealed record Gallery(string Title, string? Description, JsonElement? Images);

[ContractEvent("image-gallery.gallery.published")]
public sealed record GalleryPublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

[ContractEvent("image-gallery.gallery.unpublished")]
public sealed record GalleryUnpublished(Guid InstanceId, Guid ItemId, string ContentType, string Slug)
    : ContentChanged(InstanceId, ItemId, ContentType, Slug);

/// <summary>Published galleries of an Image gallery instance. Drafts never leave the plugin.</summary>
[DcmsContract("image-gallery.galleries", 1, Description = "Published galleries of an Image gallery instance.",
    Events = [typeof(GalleryPublished), typeof(GalleryUnpublished)])]
public interface IImageGalleries
{
    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "Published gallerys, newest first.")]
    Task<ContentPage<Gallery>> ListAsync(ContentPageRequest input, CancellationToken ct);

    [Operation(OpRisk.Read, Expose = OpExposure.Site | OpExposure.Admin | OpExposure.Ai,
        Description = "One published gallery by slug.")]
    Task<ContentEntry<Gallery>?> GetAsync(ContentSlugRequest input, CancellationToken ct);
}
