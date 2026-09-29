using Dcms.Plugins.Blog.Api;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions;

namespace Dcms.Plugins.Blog;

/// <summary>
/// Blog posts with drafts, version history and scheduled publishing. Content
/// type "post" is searchable and slugged by the title.
/// </summary>
public sealed class BlogPlugin : IPlugin
{
    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "title": "Blog title" },
            "postsPerPage": { "type": "integer", "title": "Posts per page", "minimum": 1, "maximum": 100, "default": 10 },
            "allowComments": { "type": "boolean", "title": "Allow comments", "default": false }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "blog",
        name: "Blog",
        description: "Blog posts with drafts, version history and scheduled publishing.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        permissions:
        [
            new PermissionDefinition("read", "View blog posts"),
            new PermissionDefinition("write", "Create and edit blog posts"),
        ],
        contentTypes:
        [
            new ContentTypeDefinition(
                Name: "post",
                Fields:
                [
                    new ContentFieldDefinition("title", ContentFieldType.Text, Required: true, "Post title"),
                    new ContentFieldDefinition("excerpt", ContentFieldType.Text, Required: false, "Excerpt shown in lists"),
                    new ContentFieldDefinition("body", ContentFieldType.RichText, Required: true, "Post body (rich text)"),
                    new ContentFieldDefinition("coverImage", ContentFieldType.MediaRef, Required: false, "Cover image",
                        new ContentReferenceTarget(null, null, MediaCategory.Image)),
                    new ContentFieldDefinition("tags", ContentFieldType.Tags, Required: false, "Tags"),
                ],
                Searchable: true,
                SlugField: "title",
                Published: typeof(BlogPostPublished),
                Unpublished: typeof(BlogPostUnpublished)),
        ],
        provides: [ContractProvision.Of<IBlogPosts, BlogPostsSource>()],
        consumes: [ContractRequirement.Of<IPluginContent>()],
        category: "Content",
        summary: "A dated blog with categories and an archive.",
        iconName: "Rss");
}
