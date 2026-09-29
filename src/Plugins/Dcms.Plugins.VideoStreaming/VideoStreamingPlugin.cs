using Dcms.Plugins.VideoStreaming.Api;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions;

namespace Dcms.Plugins.VideoStreaming;

/// <summary>Standalone HLS video streaming with adaptive renditions.</summary>
public sealed class VideoStreamingPlugin : IPlugin
{
    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "defaultMuted": { "type": "boolean", "default": true },
            "showControls": { "type": "boolean", "default": true }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "video-streaming",
        name: "Video Streaming",
        description: "Standalone HLS video streaming with adaptive renditions.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        permissions:
        [
            new PermissionDefinition("read", "View streams"),
            new PermissionDefinition("write", "Manage streams"),
        ],
        contentTypes:
        [
            new ContentTypeDefinition(
                Name: "stream",
                Fields:
                [
                    new ContentFieldDefinition("title", ContentFieldType.Text, Required: true, "Stream title"),
                    new ContentFieldDefinition("source", ContentFieldType.MediaRef, Required: true, "Video asset (HLS)",
                        new ContentReferenceTarget(null, null, MediaCategory.Video)),
                    new ContentFieldDefinition("poster", ContentFieldType.MediaRef, Required: false, "Poster override",
                        new ContentReferenceTarget(null, null, MediaCategory.Image)),
                ],
                Searchable: true,
                SlugField: "title",
                Published: typeof(VideoStreamPublished),
                Unpublished: typeof(VideoStreamUnpublished)),
        ],
        provides: [ContractProvision.Of<IVideoStreams, VideoStreamsSource>()],
        consumes: [ContractRequirement.Of<IPluginContent>()],
        category: "Media",
        summary: "HLS streaming for long-form video.",
        iconName: "MonitorPlay");
}
