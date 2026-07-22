using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Plugins.Roster;

namespace Dcms.PluginSdk.Tests;

public class RosterPluginTests
{
    private static PluginInstanceContext Instance(string slug = "moordoor-roster")
        => new(Guid.NewGuid(), Guid.NewGuid(), "roster", slug, "Moordoor crew", "The Moordoor crew.",
            JsonDocument.Parse("{}"));

    private static ContentTypeDefinition Member()
        => new RosterPlugin().Manifest.ContentTypes.Single(t => t.Name == "member");

    [Fact]
    public void Serves_members()
    {
        var routes = new PluginRouteTable(new PluginRegistry([new RosterPlugin()]));

        var route = routes.Find("roster", "member");
        route.Should().NotBeNull();
        route!.List.Should().BeTrue();
        route.GetBySlug.Should().BeTrue();
    }

    [Fact]
    public void Fixes_only_the_fields_every_roster_shares()
    {
        // Anything domain-specific is the tenant's to add, so this list staying
        // short is the point of the plugin, not an oversight.
        Member().Fields.Select(f => f.Name)
            .Should().BeEquivalentTo(["name", "role", "bio", "photo", "custom"]);
    }

    [Fact]
    public void Declares_where_tenant_defined_fields_are_configured_and_stored()
    {
        Member().CustomFields.Should().BeEquivalentTo(new CustomFieldsDefinition("custom", "fields"));
    }

    [Fact]
    public void Values_field_exists_on_the_content_type_it_belongs_to()
    {
        var member = Member();

        member.Fields.Select(f => f.Name).Should().Contain(member.CustomFields!.ValuesField);
    }

    [Fact]
    public void Publishes_the_field_definitions_a_site_needs_to_render_a_member()
    {
        var manifest = new RosterPlugin().Manifest;

        manifest.PublicConfigKeys.Should().Contain(manifest.ContentTypes.Single().CustomFields!.ConfigKey);
    }

    [Fact]
    public void Documents_the_member_listing_for_an_instance()
    {
        var assembler = new OpenApiAssembler(new PluginRegistry([new RosterPlugin()]));

        var doc = assembler.Build("Moordoor", [Instance()]);

        var paths = doc["paths"]!.AsObject();
        paths.Should().ContainKey("/api/moordoor-roster/member");
        paths.Should().ContainKey("/api/moordoor-roster/member/{slug}");
    }
}
