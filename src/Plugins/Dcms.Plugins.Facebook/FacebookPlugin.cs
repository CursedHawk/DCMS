using Dcms.Plugins.Meta.Core;
using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Facebook;

/// <summary>
/// A connected Facebook Page's published posts, mirrored into DCMS content by the same
/// background sync that serves <c>InstagramPlugin</c> and served by the ordinary delivery API.
///
/// <para>Separate from the Instagram plugin rather than a provider switch inside one, because
/// a tenant with only a Page should not have an Instagram feed in their OpenAPI spec, and a
/// tenant with only Instagram should not be shown Page options they cannot use.</para>
/// </summary>
public sealed class FacebookPlugin : IPlugin
{
    public const string PluginId = "facebook";
    public const string PostType = "facebook-post";

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "Facebook",
        description: "A connected Facebook Page's posts, served through this site's API.",
        allowMultipleInstances: true,
        configJsonSchema: MetaFeedConfig.Schema(
            mediaTypes: ["photo", "video", "link", "text"],
            includeReels: false,
            includeStories: false),
        permissions:
        [
            new PermissionDefinition("connect", "Connect a Facebook Page"),
            new PermissionDefinition("sync", "Trigger a Facebook sync"),
        ],
        contentTypes: [MetaFeedContentTypes.Build(PostType, "post")],
        publicConfigKeys: MetaFeedConfig.PublicKeys(includeStories: false),
        category: "Integrations",
        summary: "Mirrors a Facebook page feed into your content.",
        iconName: "Facebook");

    public void ConfigureServices(IServiceCollection services) { }

    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapContentList(PostType);
        endpoints.MapContentGetBySlug(PostType);
    }

    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
        => ContentApiFragment.ForListAndGet(instance, PostType, Manifest);
}
