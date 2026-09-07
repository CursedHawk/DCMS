using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Events;

/// <summary>
/// Live events: a "gig" is a dated performance at a venue, with a photo gallery
/// and a line-up. Upcoming/past is derived from the gig date by the consumer, so
/// the same instance serves both listings.
///
/// Performer profiles live in the Roster plugin, not here. A line-up entry always
/// carries the performer's name as plain text, so an events instance on its own is
/// complete; when <c>rosterSlug</c> names a Roster instance, entries also carry
/// that member's slug and the two sides link up.
/// </summary>
public sealed class EventsPlugin : IPlugin
{
    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "title": "Collection title" },
            "gigsPerPage": { "type": "integer", "title": "Gigs per page", "minimum": 1, "maximum": 100, "default": 20 },
            "timeZone": {
              "type": "string",
              "title": "Time zone",
              "description": "IANA time zone the gig dates are authored in, e.g. Europe/Prague.",
              "default": "UTC"
            },
            "rosterSlug": {
              "type": "string",
              "title": "Roster instance",
              "description": "Slug of the Roster instance holding performer profiles. Leave empty to keep line-ups as plain names."
            }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "events",
        name: "Events",
        description: "Live events (gigs) with line-ups and photo galleries.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        // A site needs rosterSlug to turn a line-up entry into a profile link, and
        // the rest is presentation. None of it is a credential.
        publicConfigKeys: ["title", "gigsPerPage", "timeZone", "rosterSlug"],
        // Optional: without a roster, gigs still list their line-up by name.
        dependencies: [new PluginDependency("roster", Optional: true)],
        permissions:
        [
            new PermissionDefinition("read", "View events"),
            new PermissionDefinition("write", "Create and edit events"),
        ],
        contentTypes:
        [
            new ContentTypeDefinition(
                Name: "gig",
                Fields:
                [
                    new ContentFieldDefinition("title", ContentFieldType.Text, Required: true, "Event title"),
                    new ContentFieldDefinition("date", ContentFieldType.DateTime, Required: true,
                        "When the event takes place. Dates in the future are the upcoming listing; the past listing is everything before now."),
                    new ContentFieldDefinition("venue", ContentFieldType.Text, Required: true, "Venue name"),
                    new ContentFieldDefinition("location", ContentFieldType.Text, Required: false, "City or address"),
                    new ContentFieldDefinition("attendees", ContentFieldType.Text, Required: false, "Attendance, e.g. \"500+\""),
                    new ContentFieldDefinition("description", ContentFieldType.RichText, Required: false, "Event description"),
                    new ContentFieldDefinition("genres", ContentFieldType.Tags, Required: false, "Musical genres"),
                    // Media lists are Json arrays of asset ids, matching the
                    // convention ImageGallery uses for its "images" field.
                    new ContentFieldDefinition("photos", ContentFieldType.Json, Required: false,
                        "Ordered list of photo media asset ids",
                        new ContentReferenceTarget(null, null, MediaCategory.Image)),
                    new ContentFieldDefinition("performers", ContentFieldType.Json, Required: false,
                        "Line-up: array of { memberSlug, performerName, performerRole, flyerOrder, visible }. "
                        + "performerName is always set, so a line-up reads without a roster; memberSlug is present only "
                        + "for performers picked from the Roster instance named by rosterSlug.",
                        new ContentReferenceTarget("roster", "member", null)),
                ],
                Searchable: true,
                SlugField: "title"),
        ],
        category: "Content",
        summary: "Events with dates, venues and line-ups.",
        iconName: "CalendarDays");

    public void ConfigureServices(IServiceCollection services) { }

    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapContentList("gig", options => options.DefaultPageSize = 50);
        endpoints.MapContentGetBySlug("gig");
    }

    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
        => ContentApiFragment.ForListAndGet(instance, "gig", Manifest);
}
