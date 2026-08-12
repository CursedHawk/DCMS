using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Plugins.Branding;

namespace Dcms.PluginSdk.Tests;

public class BrandingPluginTests
{
    private const string LogoAssetId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";

    private const string Config = """
        {
          "public": {
            "name": "Moordoor",
            "tagline": "Techno since 1999",
            "logo": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
            "primaryColor": "#1d4ed8",
            "items": [
              { "key": "contactEmail", "value": "hi@moordoor.example" },
              { "key": "instagram", "value": "https://instagram.com/moordoor" }
            ]
          },
          "private": {
            "items": [
              { "key": "stripeAccount", "value": "acct_123" }
            ]
          }
        }
        """;

    private static PluginInstanceContext Instance(string config)
        => new(Guid.NewGuid(), Guid.NewGuid(), "branding", "moordoor-branding", "Moordoor branding",
            "Branding for the Moordoor site.", JsonDocument.Parse(config));

    [Fact]
    public void Reads_scalar_fields_and_media_asset_ids_from_the_public_section()
    {
        var branding = BrandingPlugin.ReadBranding(JsonDocument.Parse(Config));

        branding.Name.Should().Be("Moordoor");
        branding.Tagline.Should().Be("Techno since 1999");
        // Logo/favicon are stored as media asset ids, resolved to URLs at delivery.
        branding.LogoAssetId.Should().Be(LogoAssetId);
        branding.PrimaryColor.Should().Be("#1d4ed8");
        branding.LogoDarkAssetId.Should().BeNull();
        branding.FaviconAssetId.Should().BeNull();
    }

    [Fact]
    public void Flattens_public_and_private_items_into_separate_maps()
    {
        var branding = BrandingPlugin.ReadBranding(JsonDocument.Parse(Config));

        branding.Items.Should().ContainKey("contactEmail").WhoseValue.Should().Be("hi@moordoor.example");
        branding.Items.Should().ContainKey("instagram");
        branding.Items.Should().NotContainKey("stripeAccount");

        branding.PrivateItems.Should().ContainKey("stripeAccount").WhoseValue.Should().Be("acct_123");
        branding.PrivateItems.Should().NotContainKey("contactEmail");
    }

    [Fact]
    public void Reads_empty_branding_from_an_unconfigured_instance()
    {
        var branding = BrandingPlugin.ReadBranding(JsonDocument.Parse("{}"));

        branding.Name.Should().BeNull();
        branding.Items.Should().BeEmpty();
        branding.PrivateItems.Should().BeEmpty();
    }

    [Fact]
    public void Documents_a_public_only_branding_read_endpoint()
    {
        var assembler = new OpenApiAssembler(new PluginRegistry([new BrandingPlugin()]));

        var doc = assembler.Build("Moordoor", [Instance(Config)]);

        var get = doc["paths"]!["/api/moordoor-branding/branding"]!["get"]!;
        get.Should().NotBeNull();
        get["description"]!.GetValue<string>().Should().Contain("private branding section is");

        var schema = doc["components"]!["schemas"]!["moordoor-branding_branding"]!;
        schema["properties"]!.AsObject().Should()
            .ContainKeys("name", "tagline", "logoUrl", "primaryColor", "items");
    }

    [Fact]
    public void Declares_no_content_routes()
    {
        // Branding is read from config by content-api, not published content.
        var routes = new PluginRouteTable(new PluginRegistry([new BrandingPlugin()]));

        routes.Find("branding", "branding").Should().BeNull();
    }

    [Fact]
    public void Exposes_only_the_public_section_as_a_public_config_key()
    {
        var manifest = new BrandingPlugin().Manifest;

        // The private section is deliberately absent so it can never leak via /_config.
        manifest.PublicConfigKeys.Should().BeEquivalentTo(["public"]);
    }
}
