using System.Text.Json;
using Dcms.Plugins.Facebook;
using Dcms.Plugins.Instagram;
using Dcms.Plugins.Meta.Core;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;

namespace Dcms.PluginSdk.Tests;

/// <summary>
/// Manifest-level guards for the two Meta feed plugins.
///
/// <para>These matter more than the usual manifest test because the manifest is what drives
/// the public API document and the public config endpoint: a wrong entry here does not throw,
/// it publishes something.</para>
/// </summary>
public class MetaFeedPluginTests
{
    private static PluginInstanceContext Instance(string pluginId, string config = "{}")
        => new(Guid.NewGuid(), Guid.NewGuid(), pluginId, "feed", "Our feed",
            "The bakery's social feed.", JsonDocument.Parse(config));

    [Fact]
    public void Neither_plugin_can_publish_a_credential_through_the_public_config_endpoint()
    {
        // GET /api/{slug}/_config serves exactly the keys named here, so this list is the
        // boundary between tenant-private config and the open internet. connectionId is the
        // handle to a stored OAuth token and has no business on a public site.
        foreach (var manifest in new[] { new InstagramPlugin().Manifest, new FacebookPlugin().Manifest })
        {
            manifest.PublicConfigKeys.Should().NotContain(MetaFeedConfig.ConnectionIdKey);
            manifest.PublicConfigKeys.Should().OnlyContain(
                k => k == MetaFeedConfig.AccountUsernameKey || k == MetaFeedConfig.ShowStoriesKey);
        }

        // Facebook has no stories concept at all, so exposing the key would advertise
        // something its schema never declares.
        new FacebookPlugin().Manifest.PublicConfigKeys.Should().NotContain(MetaFeedConfig.ShowStoriesKey);
    }

    [Fact]
    public void Every_sync_cap_is_bounded_in_the_schema()
    {
        // A cap without a maximum is a cap in name only: the admin form would accept 100000
        // and the sync would happily try to honour it.
        foreach (var manifest in new[] { new InstagramPlugin().Manifest, new FacebookPlugin().Manifest })
        {
            using var schema = JsonDocument.Parse(manifest.ConfigJsonSchema);
            var properties = schema.RootElement.GetProperty("properties");

            foreach (var key in new[] { MetaFeedConfig.MaxPostsKey, MetaFeedConfig.MaxReelsKey })
            {
                if (!properties.TryGetProperty(key, out var cap)) continue;

                cap.TryGetProperty("maximum", out var max).Should().BeTrue($"{key} must be bounded");
                max.GetInt32().Should().Be(MetaFeedConfig.MaxItemsCeiling);
                cap.GetProperty("minimum").GetInt32().Should().Be(0);
                cap.TryGetProperty("default", out _).Should().BeTrue($"{key} must have a default");
            }
        }
    }

    [Fact]
    public void Instagram_documents_posts_and_reels_but_only_offers_stories_when_they_are_switched_on()
    {
        var plugin = new InstagramPlugin();

        var off = plugin.BuildOpenApiFragment(Instance("instagram"));
        off.Paths.Should().Contain(p => p.RelativePath.Contains(InstagramPlugin.PostType));
        off.Paths.Should().Contain(p => p.RelativePath.Contains(InstagramPlugin.ReelType));
        // The spec has to describe what this site actually serves. Advertising a stories path
        // an instance has switched off sends AI consumers and generated clients at a 404.
        off.Paths.Should().NotContain(p => p.RelativePath.Contains(InstagramPlugin.StoryType));

        var on = plugin.BuildOpenApiFragment(Instance("instagram", """{ "showStories": true }"""));
        on.Paths.Should().Contain(p => p.RelativePath.Contains(InstagramPlugin.StoryType));
        // Stories are list-only: a story is gone within 24 hours, so a permalinked detail
        // route would document something that reliably 404s.
        on.Paths.Should().NotContain(p =>
            p.RelativePath.Contains(InstagramPlugin.StoryType) && p.RelativePath.Contains('{'));
    }

    [Fact]
    public void Story_content_is_declared_as_a_type_but_never_routed_through_generic_delivery()
    {
        var plugin = new InstagramPlugin();

        // Declared, so the builder generates a block for it and the OpenAPI fragment can
        // describe it in the ordinary paged shape...
        plugin.Manifest.ContentTypes.Should().Contain(c => c.Name == InstagramPlugin.StoryType);

        // ...but not registered for generic delivery, because nothing ever writes story rows.
        // If it were, /api/{slug}/instagram-story would return an empty list from an empty
        // table instead of the live feed, which is the failure that looks like "it works".
        var routes = new PluginRouteTable(new PluginRegistry([plugin]));
        routes.Find(InstagramPlugin.PluginId, InstagramPlugin.StoryType).Should().BeNull();
        routes.Find(InstagramPlugin.PluginId, InstagramPlugin.PostType).Should().NotBeNull();
    }

    [Fact]
    public void Both_providers_expose_the_same_field_names_so_one_site_template_fits_either()
    {
        var instagram = new InstagramPlugin().Manifest.ContentTypes
            .Single(c => c.Name == InstagramPlugin.PostType).Fields.Select(f => f.Name);
        var facebook = new FacebookPlugin().Manifest.ContentTypes
            .Single(c => c.Name == FacebookPlugin.PostType).Fields.Select(f => f.Name);

        facebook.Should().BeEquivalentTo(instagram);
    }
}
