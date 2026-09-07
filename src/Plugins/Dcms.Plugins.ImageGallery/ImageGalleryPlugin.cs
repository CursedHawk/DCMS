using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.ImageGallery;

/// <summary>
/// Image galleries. A "gallery" groups images (referenced by media asset id);
/// originals are stored and webp variants are served through the media API.
/// </summary>
public sealed class ImageGalleryPlugin : IPlugin
{
    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "layout": { "type": "string", "enum": ["grid", "masonry", "carousel"], "default": "grid" },
            "columns": { "type": "integer", "minimum": 1, "maximum": 8, "default": 3 }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "image-gallery",
        name: "Image Gallery",
        description: "Image galleries served as webp variants with the original preserved.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        permissions:
        [
            new PermissionDefinition("read", "View galleries"),
            new PermissionDefinition("write", "Create and edit galleries"),
        ],
        contentTypes:
        [
            new ContentTypeDefinition(
                Name: "gallery",
                Fields:
                [
                    new ContentFieldDefinition("title", ContentFieldType.Text, Required: true, "Gallery title"),
                    new ContentFieldDefinition("description", ContentFieldType.Text, Required: false, "Gallery description"),
                    new ContentFieldDefinition("images", ContentFieldType.Json, Required: false,
                        "Ordered list of image media asset ids",
                        new ContentReferenceTarget(null, null, MediaCategory.Image)),
                ],
                Searchable: true,
                SlugField: "title"),
        ],
        category: "Media",
        summary: "Image galleries served as webp variants, originals preserved.",
        iconName: "Images");

    public void ConfigureServices(IServiceCollection services) { }

    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapContentList("gallery");
        endpoints.MapContentGetBySlug("gallery");
    }

    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
        => ContentApiFragment.ForListAndGet(instance, "gallery", Manifest);
}
