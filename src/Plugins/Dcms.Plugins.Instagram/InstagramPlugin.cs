using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.Meta.Core;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Plugins.Instagram.Api;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Instagram;

/// <summary>
/// A tenant's Instagram feed, served from their own site.
///
/// <para>Posts and reels are mirrored into DCMS content by a background sync (Meta.Core), so
/// they are ordinary published items: cached, searchable, droppable onto a page in the builder
/// and documented in the tenant's OpenAPI spec, with no delivery code of their own.</para>
///
/// <para>Stories are the exception. They expire after 24 hours, which makes syncing them into
/// content a poor fit, so they are fetched live by a dedicated site route — one that
/// deliberately answers on the same <c>/api/{slug}/instagram-story</c> path and in the same
/// paged shape as a real content type, so the generated builder block and the site runtime
/// cannot tell the difference.</para>
/// </summary>
public sealed class InstagramPlugin : IPlugin
{
    public const string PluginId = MetaPlugins.InstagramId;
    public const string PostType = MetaPlugins.InstagramPost;
    public const string ReelType = MetaPlugins.InstagramReel;
    public const string StoryType = MetaPlugins.InstagramStory;

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
            new PermissionDefinition("connect", "Connect an Instagram account",
                "Link or unlink the Instagram account whose posts appear on your sites."),
            new PermissionDefinition("sync", "Trigger an Instagram sync"),
        ],
        contentTypes:
        [
            MetaFeedContentTypes.Build(PostType, "post") with
            {
                Published = typeof(InstagramPostPublished), Unpublished = typeof(InstagramPostUnpublished),
            },
            MetaFeedContentTypes.Build(ReelType, "reel") with
            {
                Published = typeof(InstagramReelPublished), Unpublished = typeof(InstagramReelUnpublished),
            },
            MetaFeedContentTypes.Build(StoryType, "story"),
        ],
        publicConfigKeys: MetaFeedConfig.PublicKeys(includeStories: true),
        provides:
        [
            ContractProvision.Of<IInstagramPosts, InstagramPostsSource>(),
            ContractProvision.Of<IInstagramReels, InstagramReelsSource>(),
        ],
        consumes:
        [
            ContractRequirement.Of<IPluginContent>(),
            // The sync mirrors Meta's files through it; reauth notices go through the bell.
            ContractRequirement.Of<IPluginMedia>(),
            ContractRequirement.Of<IPluginNotifications>(),
        ],
        category: "Integrations",
        summary: "Mirrors an Instagram feed into your content.",
        iconName: "Instagram");


    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapContentList(PostType);
        endpoints.MapContentGetBySlug(PostType);
        endpoints.MapContentList(ReelType);
        endpoints.MapContentGetBySlug(ReelType);

        // Stories are deliberately NOT declared as a content route. They are fetched live
        // (StoryDeliveryEndpoints), and a literal route segment already outranks the generic
        // /api/{slug}/{contentType} handler — but leaving the declaration out means that even
        // if that precedence ever changed, the generic handler would 404 rather than quietly
        // return an empty list from a table nothing writes to.
        StoryDeliveryEndpoints.Map(endpoints);
    }

    public void ConfigureServices(IServiceCollection services, PluginHost host) => MetaSocial.AddServices(services, host);

    public void MapHostEndpoints(IEndpointRouteBuilder app, PluginHost host) => MetaSocial.MapHostEndpoints(app, host);

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

    public static bool StoriesEnabled(JsonDocument config) => MetaPlugins.StoriesEnabled(config);
}
