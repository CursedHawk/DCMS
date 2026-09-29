using Dcms.Plugins.Carousel.Api;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions;

namespace Dcms.Plugins.Carousel;

/// <summary>Rotating image/content carousel. Each "slide" references an image and an optional link.</summary>
public sealed class CarouselPlugin : IPlugin
{
    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "autoplay": { "type": "boolean", "default": true },
            "intervalMs": { "type": "integer", "minimum": 1000, "maximum": 30000, "default": 5000 }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "carousel",
        name: "Carousel",
        description: "Rotating image and content carousel for landing pages.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        // Playback behaviour is presentation-only, so a site may read it and
        // drive the carousel entirely from the instance config.
        publicConfigKeys: ["autoplay", "intervalMs"],
        permissions:
        [
            new PermissionDefinition("read", "View carousel slides"),
            new PermissionDefinition("write", "Create and edit carousel slides"),
        ],
        contentTypes:
        [
            new ContentTypeDefinition(
                Name: "slide",
                Fields:
                [
                    new ContentFieldDefinition("title", ContentFieldType.Text, Required: true, "Slide title"),
                    new ContentFieldDefinition("caption", ContentFieldType.Text, Required: false, "Caption"),
                    new ContentFieldDefinition("image", ContentFieldType.MediaRef, Required: true, "Slide image",
                        new ContentReferenceTarget(null, null, MediaCategory.Image)),
                    new ContentFieldDefinition("linkUrl", ContentFieldType.Text, Required: false, "Click-through URL"),
                    new ContentFieldDefinition("order", ContentFieldType.Number, Required: false, "Display order"),
                ],
                Searchable: false,
                SlugField: "title",
                Published: typeof(CarouselSlidePublished),
                Unpublished: typeof(CarouselSlideUnpublished)),
        ],
        provides: [ContractProvision.Of<ICarouselSlides, CarouselSlidesSource>()],
        consumes: [ContractRequirement.Of<IPluginContent>()],
        category: "Presentation",
        summary: "Rotating banners for a page hero.",
        iconName: "GalleryHorizontalEnd");
}
