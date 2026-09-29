using Dcms.Plugins.VideoGallery.Api;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions;

namespace Dcms.Plugins.VideoGallery;

/// <summary>Video collections with HLS playback and poster images.</summary>
public sealed class VideoGalleryPlugin : IPlugin
{
    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "columns": { "type": "integer", "minimum": 1, "maximum": 6, "default": 3 },
            "autoplay": { "type": "boolean", "default": false }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "video-gallery",
        name: "Video Gallery",
        description: "Video collections with HLS playback and poster images.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        permissions:
        [
            new PermissionDefinition("read", "View videos"),
            new PermissionDefinition("write", "Manage videos"),
        ],
        contentTypes:
        [
            new ContentTypeDefinition(
                Name: "video",
                Fields:
                [
                    new ContentFieldDefinition("title", ContentFieldType.Text, Required: true, "Video title"),
                    new ContentFieldDefinition("description", ContentFieldType.Text, Required: false, "Description"),
                    new ContentFieldDefinition("source", ContentFieldType.MediaRef, Required: true, "Video asset (HLS)",
                        new ContentReferenceTarget(null, null, MediaCategory.Video)),
                ],
                Searchable: true,
                SlugField: "title",
                Published: typeof(GalleryVideoPublished),
                Unpublished: typeof(GalleryVideoUnpublished)),
        ],
        provides: [ContractProvision.Of<IGalleryVideos, GalleryVideosSource>()],
        consumes: [ContractRequirement.Of<IPluginContent>()],
        category: "Media",
        summary: "Video collections with posters and captions.",
        iconName: "Video");
}
