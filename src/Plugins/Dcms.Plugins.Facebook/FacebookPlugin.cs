using Dcms.Plugins.Meta.Core;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Plugins.Facebook.Api;
using Microsoft.AspNetCore.Routing;
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
    public const string PluginId = MetaPlugins.FacebookId;
    public const string PostType = MetaPlugins.FacebookPost;

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
            new PermissionDefinition("connect", "Connect a Facebook Page",
                "Link or unlink the Facebook Page whose posts appear on your sites."),
            new PermissionDefinition("sync", "Trigger a Facebook sync"),
        ],
        contentTypes:
        [
            MetaFeedContentTypes.Build(PostType, "post") with
            {
                Published = typeof(FacebookPostPublished), Unpublished = typeof(FacebookPostUnpublished),
            },
        ],
        provides: [ContractProvision.Of<IFacebookPosts, FacebookPostsSource>()],
        consumes:
        [
            ContractRequirement.Of<IPluginContent>(),
            ContractRequirement.Of<IPluginMedia>(),
            ContractRequirement.Of<IPluginNotifications>(),
        ],
        publicConfigKeys: MetaFeedConfig.PublicKeys(includeStories: false),
        category: "Integrations",
        summary: "Mirrors a Facebook page feed into your content.",
        iconName: "Facebook");


    public void ConfigureServices(IServiceCollection services, PluginHost host) => MetaSocial.AddServices(services, host);

    public void MapHostEndpoints(IEndpointRouteBuilder app, PluginHost host) => MetaSocial.MapHostEndpoints(app, host);
}
