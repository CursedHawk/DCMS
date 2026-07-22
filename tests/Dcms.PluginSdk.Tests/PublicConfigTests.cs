using AwesomeAssertions;
using Dcms.Plugins.All;
using Dcms.Plugins.Carousel;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.PluginSdk.Tests;

/// <summary>
/// Instance config is tenant-private unless a plugin opts in, so these guard the
/// default rather than just the Carousel case: GET /api/{slug}/_config returns
/// only the keys listed here.
/// </summary>
public class PublicConfigTests
{
    private static IReadOnlyList<PluginManifest> AllManifests()
    {
        var services = new ServiceCollection();
        services.AddDcmsPlugins(plugins => plugins.AddAll());
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<PluginRegistry>().Manifests;
    }

    [Fact]
    public void Carousel_exposes_only_its_playback_settings()
    {
        var manifest = new CarouselPlugin().Manifest;

        manifest.PublicConfigKeys.Should().BeEquivalentTo(["autoplay", "intervalMs"]);
    }

    [Fact]
    public void Every_public_config_key_is_declared_in_the_plugins_schema()
    {
        foreach (var manifest in AllManifests())
        {
            foreach (var key in manifest.PublicConfigKeys)
            {
                manifest.ConfigJsonSchema.Should().Contain($"\"{key}\"",
                    $"'{key}' is exposed publicly by '{manifest.Id}' but is not a key in its config schema");
            }
        }
    }

    [Fact]
    public void Plugins_expose_no_config_unless_they_opt_in()
    {
        var exposing = AllManifests()
            .Where(m => m.PublicConfigKeys.Count > 0)
            .Select(m => m.Id)
            .ToList();

        // Adding a plugin here is a deliberate act: the keys become world-readable
        // on every published tenant site.
        exposing.Should().BeEquivalentTo(["carousel", "events", "roster"]);
    }
}
