using Dcms.Plugins.FileDownloads.Api;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions;

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
                SlugField: "title",
                Published: typeof(DownloadFilePublished),
                Unpublished: typeof(DownloadFileUnpublished)),
        ],
        provides: [ContractProvision.Of<IDownloadFiles, DownloadFilesSource>()],
        consumes: [ContractRequirement.Of<IPluginContent>()],
        category: "Media",
        summary: "Downloadable files with counts and access rules.",
        iconName: "Download");
}
