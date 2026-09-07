using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.AudioLibrary;

/// <summary>Audio collections with AAC playback and waveform peaks.</summary>
public sealed class AudioLibraryPlugin : IPlugin
{
    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "showWaveform": { "type": "boolean", "default": true }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "audio-library",
        name: "Audio Library",
        description: "Audio collections with AAC playback variants and waveform data.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        permissions:
        [
            new PermissionDefinition("read", "View audio"),
            new PermissionDefinition("write", "Manage audio"),
        ],
        contentTypes:
        [
            new ContentTypeDefinition(
                Name: "track",
                Fields:
                [
                    new ContentFieldDefinition("title", ContentFieldType.Text, Required: true, "Track title"),
                    new ContentFieldDefinition("artist", ContentFieldType.Text, Required: false, "Artist"),
                    new ContentFieldDefinition("source", ContentFieldType.MediaRef, Required: true, "Audio asset",
                        new ContentReferenceTarget(null, null, MediaCategory.Audio)),
                ],
                Searchable: true,
                SlugField: "title"),
        ],
        category: "Media",
        summary: "Audio tracks with artwork and transcoded variants.",
        iconName: "Music");

    public void ConfigureServices(IServiceCollection services) { }

    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapContentList("track");
        endpoints.MapContentGetBySlug("track");
    }

    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
        => ContentApiFragment.ForListAndGet(instance, "track", Manifest);
}
