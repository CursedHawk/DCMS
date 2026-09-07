using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.Meta.Core;
using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Instagram;

/// <summary>
/// A tenant's Instagram feed, served from their own site.
///
/// <para>Posts and reels are mirrored into DCMS content by a background sync in admin-api, so
/// they are ordinary published items: cached, searchable, droppable onto a page in the builder
/// and documented in the tenant's OpenAPI spec, with no delivery code of their own.</para>
///
/// <para>Stories are the exception. They expire after 24 hours, which makes syncing them into
/// content a poor fit, so they are fetched live by a dedicated content-api endpoint — one that
/// deliberately answers on the same <c>/api/{slug}/instagram-story</c> path and in the same
/// paged shape as a real content type, so the generated builder block and the site runtime
/// cannot tell the difference.</para>
/// </summary>
public sealed class InstagramPlugin : IPlugin
{
    public const string PluginId = "instagram";
    public const string PostType = "instagram-post";
    public const string ReelType = "instagram-reel";
    public const string StoryType = "instagram-story";

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "Instagram",
        description: "A connected Instagram account's posts, reels and stories, served through this site's API.",
        allowMultipleInstances: true,
        configJsonSchema: MetaFeedConfig.Schema(
            mediaTypes: ["photo", "carousel", "video"],
            includeReels: true,
            includeStories: true),
        permissions:
        [
            new PermissionDefinition("connect", "Connect an Instagram account"),
            new PermissionDefinition("sync", "Trigger an Instagram sync"),
        ],
        contentTypes:
        [
            MetaFeedContentTypes.Build(PostType, "post"),
            MetaFeedContentTypes.Build(ReelType, "reel"),
            MetaFeedContentTypes.Build(StoryType, "story"),
        ],
        publicConfigKeys: MetaFeedConfig.PublicKeys(includeStories: true),
        category: "Integrations",
        summary: "Mirrors an Instagram feed into your content.",
        iconName: "Instagram");

    public void ConfigureServices(IServiceCollection services) { }

    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapContentList(PostType);
        endpoints.MapContentGetBySlug(PostType);
        endpoints.MapContentList(ReelType);
        endpoints.MapContentGetBySlug(ReelType);

        // Stories are deliberately NOT declared here. They are served by a live endpoint in
        // content-api, and a literal route segment already outranks the generic
        // /api/{slug}/{contentType} handler — but leaving the declaration out means that even
        // if that precedence ever changed, the generic handler would 404 rather than quietly
        // return an empty list from a table nothing writes to.
    }

    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
    {
        var posts = ContentApiFragment.ForListAndGet(instance, PostType, Manifest);
        var reels = ContentApiFragment.ForListAndGet(instance, ReelType, Manifest);
        var stories = ContentApiFragment.ForListAndGet(instance, StoryType, Manifest);

        var paths = posts.Paths.Concat(reels.Paths).ToList();
        var schemas = new Dictionary<string, JsonNode>(posts.Schemas);
        foreach (var (key, value) in reels.Schemas) schemas[key] = value;

        // Only advertise stories when the instance has them switched on, so the spec describes
        // what this site actually serves rather than what the plugin could do.
        if (StoriesEnabled(instance.Config))
        {
            // The list operation only: a story has no stable detail URL, because by the time
            // anyone followed one it would very likely be gone.
            paths.AddRange(stories.Paths.Where(p => !p.RelativePath.Contains('{')));
            foreach (var (key, value) in stories.Schemas) schemas[key] = value;
        }

        return new OpenApiFragment(posts.TagName, posts.TagDescription, paths, schemas);
    }

    /// <summary>
    /// Reads the stories toggle out of instance config, tolerating an absent or malformed
    /// value. Absence means off: an instance that has never been configured must not start
    /// advertising a live Meta call in its public API document.
    /// </summary>
    public static bool StoriesEnabled(JsonDocument config) =>
        config.RootElement.ValueKind == JsonValueKind.Object
        && config.RootElement.TryGetProperty(MetaFeedConfig.ShowStoriesKey, out var value)
        && value.ValueKind == JsonValueKind.True;
}
