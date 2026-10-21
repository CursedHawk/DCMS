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

    private sealed class Screens(params AdminScreen[] screens) : IPlugin
    {
        public PluginManifest Manifest { get; } = PluginManifest.Create("screens", "Screens", "", false, adminScreens: screens);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("integration")]
    [InlineData("activity")]
    [InlineData("content")]
    public void A_screen_cannot_take_a_page_the_console_draws_in_every_app(string id)
    {
        var act = () => new PluginRegistry([new Screens(new AdminScreen(id, "Mine", AdminScreenScope.Instance))]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*console's own page*");
    }

    [Fact]
    public void Settings_are_an_instance_screen_under_admin()
    {
        var plugin = () => new PluginRegistry([new Screens(new AdminScreen(AdminScreen.SettingsId, "Settings", AdminScreenScope.Plugin))]);
        plugin.Should().Throw<InvalidOperationException>().WithMessage("*Instance screen in the Admin section*");
        var work = () => new PluginRegistry([new Screens(new AdminScreen(AdminScreen.SettingsId, "Settings", AdminScreenScope.Instance))]);
        work.Should().Throw<InvalidOperationException>();

        new PluginRegistry([new Screens(AdminScreen.Settings)]).Manifests.Should().ContainSingle();
    }
}
