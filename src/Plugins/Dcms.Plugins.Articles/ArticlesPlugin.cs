using Dcms.Plugins.Articles.Api;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions;

namespace Dcms.Plugins.Articles;

/// <summary>
/// Long-form articles with hero media and related-article references. Content
/// type "article" is searchable and slugged by the title.
/// </summary>
public sealed class ArticlesPlugin : IPlugin
{
    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "showAuthor": { "type": "boolean", "title": "Show author", "default": true },
            "articlesPerPage": { "type": "integer", "title": "Articles per page", "minimum": 1, "maximum": 100, "default": 10 }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "articles",
        name: "Articles",
        description: "Long-form articles with related-article links and hero media.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        permissions:
        [
            new PermissionDefinition("read", "View articles"),
            new PermissionDefinition("write", "Create and edit articles"),
        ],
        contentTypes:
        [
            new ContentTypeDefinition(
                Name: "article",
                Fields:
                [
                    new ContentFieldDefinition("title", ContentFieldType.Text, Required: true, "Article title"),
                    new ContentFieldDefinition("summary", ContentFieldType.Text, Required: false, "Short summary shown in lists"),
                    new ContentFieldDefinition("body", ContentFieldType.Markdown, Required: true, "Article body (Markdown)"),
                    new ContentFieldDefinition("heroImage", ContentFieldType.MediaRef, Required: false, "Hero image",
                        new ContentReferenceTarget(null, null, MediaCategory.Image)),
                    new ContentFieldDefinition("related", ContentFieldType.ContentRef, Required: false, "Related articles",
                        new ContentReferenceTarget("articles", "article", null)),
                    new ContentFieldDefinition("tags", ContentFieldType.Tags, Required: false, "Tags"),
                ],
                Searchable: true,
                SlugField: "title",
                Published: typeof(ArticlePublished),
                Unpublished: typeof(ArticleUnpublished)),
        ],
        provides: [ContractProvision.Of<IArticles, ArticlesSource>()],
        consumes: [ContractRequirement.Of<IPluginContent>()],
        category: "Content",
        summary: "Long-form articles with authors, tags and scheduled publishing.",
        iconName: "Newspaper");
}
