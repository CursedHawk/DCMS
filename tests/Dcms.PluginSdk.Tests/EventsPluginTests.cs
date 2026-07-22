using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Runtime;
using Dcms.Plugins.Events;

namespace Dcms.PluginSdk.Tests;

public class EventsPluginTests
{
    private static PluginInstanceContext Instance(string slug = "moordoor")
        => new(Guid.NewGuid(), Guid.NewGuid(), "events", slug, "Moordoor", "The Moordoor crew and their gigs.",
            JsonDocument.Parse("{}"));

    [Fact]
    public void Serves_gigs_only_performers_live_in_the_roster_plugin()
    {
        var registry = new PluginRegistry([new EventsPlugin()]);

        var manifest = registry.Find("events");
        manifest.Should().NotBeNull();
        manifest!.ContentTypes.Select(c => c.Name).Should().BeEquivalentTo(["gig"]);
    }

    [Fact]
    public void Depends_on_the_roster_plugin_optionally()
    {
        // Optional is the contract that lets a line-up degrade to plain names.
        var manifest = new EventsPlugin().Manifest;

        manifest.Dependencies.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new PluginDependency("roster", Optional: true));
    }

    [Fact]
    public void Declares_list_and_get_routes_for_gigs()
    {
        var routes = new PluginRouteTable(new PluginRegistry([new EventsPlugin()]));

        var route = routes.Find("events", "gig");
        route.Should().NotBeNull();
        route!.List.Should().BeTrue();
        route.GetBySlug.Should().BeTrue();
    }

    [Fact]
    public void No_longer_serves_crew_members()
    {
        var routes = new PluginRouteTable(new PluginRegistry([new EventsPlugin()]));

        routes.Find("events", "crewMember").Should().BeNull();
    }

    [Fact]
    public void Documents_the_gig_listing_for_an_instance()
    {
        var assembler = new OpenApiAssembler(new PluginRegistry([new EventsPlugin()]));

        var doc = assembler.Build("Moordoor", [Instance()]);

        var paths = doc["paths"]!.AsObject();
        paths.Should().ContainKey("/api/moordoor/gig");
        paths.Should().ContainKey("/api/moordoor/gig/{slug}");
    }

    [Fact]
    public void Gig_schema_carries_the_photo_and_line_up_fields_the_site_reads()
    {
        var assembler = new OpenApiAssembler(new PluginRegistry([new EventsPlugin()]));

        var doc = assembler.Build("Moordoor", [Instance()]);

        var data = doc["components"]!["schemas"]!["moordoor_gig"]!["properties"]!["data"]!["properties"]!.AsObject();
        data.Should().ContainKeys("title", "date", "venue", "photos", "performers", "genres");
    }

    [Fact]
    public void Line_up_points_at_roster_members()
    {
        var performers = new EventsPlugin().Manifest.ContentTypes
            .Single(t => t.Name == "gig").Fields
            .Single(f => f.Name == "performers");

        performers.Reference.Should().BeEquivalentTo(new ContentReferenceTarget("roster", "member", null));
    }
}
