using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.FileDownloads;

/// <summary>Curated downloadable files. Each "file" references a sanitized media asset.</summary>
public sealed class FileDownloadsPlugin : IPlugin
{
    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "showFileSize": { "type": "boolean", "default": true },
            "groupByCategory": { "type": "boolean", "default": false }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "file-downloads",
        name: "File Downloads",
        description: "Curated downloadable file lists with sanitized originals.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        permissions:
        [
            new PermissionDefinition("read", "View downloads"),
            new PermissionDefinition("write", "Manage downloads"),
        ],
        contentTypes:
        [
            new ContentTypeDefinition(
                Name: "file",
                Fields:
                [
                    new ContentFieldDefinition("title", ContentFieldType.Text, Required: true, "Display title"),
                    new ContentFieldDefinition("description", ContentFieldType.Text, Required: false, "Description"),
                    new ContentFieldDefinition("asset", ContentFieldType.MediaRef, Required: true, "Downloadable file",
                        new ContentReferenceTarget(null, null, MediaCategory.File)),
                    new ContentFieldDefinition("category", ContentFieldType.Text, Required: false, "Category"),
                ],
                Searchable: true,
                SlugField: "title"),
        ]);

    public void ConfigureServices(IServiceCollection services) { }

    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapContentList("file");
        endpoints.MapContentGetBySlug("file");
    }

    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
        => ContentApiFragment.ForListAndGet(instance, "file", Manifest);
}
