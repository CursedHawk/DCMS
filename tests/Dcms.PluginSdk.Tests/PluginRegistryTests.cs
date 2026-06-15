using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Plugins.Analytics;
using Dcms.Plugins.Articles;
using Dcms.Plugins.AudioLibrary;
using Dcms.Plugins.Blog;
using Dcms.Plugins.Carousel;
using Dcms.Plugins.FileDownloads;
using Dcms.Plugins.ImageGallery;
using Dcms.Plugins.LiveChat;
using Dcms.Plugins.Search;
using Dcms.Plugins.VideoGallery;
using Dcms.Plugins.VideoStreaming;
using Dcms.Plugins.VisitorAuth;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests;

public class PluginRegistryTests
{
    private static readonly IPlugin[] AllPlugins =
    [
        new ArticlesPlugin(),
        new AnalyticsPlugin(),
        new AudioLibraryPlugin(),
        new BlogPlugin(),
        new CarouselPlugin(),
        new FileDownloadsPlugin(),
        new ImageGalleryPlugin(),
        new LiveChatPlugin(),
        new SearchPlugin(),
        new VideoGalleryPlugin(),
        new VideoStreamingPlugin(),
        new VisitorAuthPlugin(),
    ];

    [Fact]
    public void All_twelve_plugins_register_with_unique_kebab_case_ids()
    {
        var registry = new PluginRegistry(AllPlugins);

        registry.Manifests.Should().HaveCount(12);
        registry.Manifests.Select(m => m.Id).Should().OnlyHaveUniqueItems();
        registry.Manifests.Should().AllSatisfy(m => PluginRegistry.IsKebabCase(m.Id).Should().BeTrue());
    }

    [Fact]
    public void Duplicate_plugin_id_is_rejected()
    {
        var act = () => new PluginRegistry([new BlogPlugin(), new BlogPlugin()]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate*");
    }

    [Fact]
    public void Single_instance_plugins_are_flagged()
    {
        var registry = new PluginRegistry(AllPlugins);

        var singletons = registry.Manifests
            .Where(m => !m.AllowMultipleInstances)
            .Select(m => m.Id);
        singletons.Should().BeEquivalentTo(["search", "analytics", "visitor-auth", "live-chat"]);
    }

    [Fact]
    public void AddDcmsPlugins_exposes_catalog_via_di()
    {
        var services = new ServiceCollection();
        services.AddDcmsPlugins(plugins => plugins.Add<BlogPlugin>().Add<ArticlesPlugin>());

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IPluginCatalog>();

        catalog.Manifests.Should().HaveCount(2);
        catalog.Find("blog").Should().NotBeNull();
        catalog.Find("missing").Should().BeNull();
    }
}
