using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Roster;

/// <summary>
/// A list of people (or anything else with a name and a picture): a band's crew,
/// a company's staff, a conference's speakers.
///
/// The plugin fixes only the handful of fields every roster has — name, role,
/// photo, biography. Everything domain-specific is added by the tenant admin as
/// a custom field on the instance config, which is what lets one plugin serve a
/// DJ crew and a legal team without a code change. Definitions and values are
/// both public, so a site can render an unknown roster without being rebuilt.
/// </summary>
public sealed class RosterPlugin : IPlugin
{
    // "fields" is the admin-defined schema. Its "key" pattern matches what a
    // consumer can safely use as a JSON property and a template variable, and
    // "type" is a closed set so a site can switch on it exhaustively.
    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "title": { "type": "string", "title": "Roster title" },
            "memberNoun": {
              "type": "string",
              "title": "Member noun",
              "description": "What one entry is called in this roster, e.g. \"DJ\", \"Speaker\", \"Staff member\".",
              "default": "Member"
            },
            "fields": {
              "type": "array",
              "title": "Custom fields",
              "description": "Extra fields every member of this roster can fill in.",
              "items": {
                "type": "object",
                "properties": {
                  "key": {
                    "type": "string",
                    "title": "Key",
                    "description": "Property name the API returns this field under. Letters, digits and underscores.",
                    "pattern": "^[A-Za-z][A-Za-z0-9_]*$"
                  },
                  "label": { "type": "string", "title": "Label", "description": "Shown to authors and, usually, on the site." },
                  "type": {
                    "type": "string",
                    "title": "Type",
                    "enum": ["text", "longText", "number", "boolean", "date", "tags", "url", "image"],
                    "default": "text"
                  },
                  "description": { "type": "string", "title": "Description", "description": "Help text shown under the input." },
                  "required": { "type": "boolean", "title": "Required", "default": false }
                },
                "required": ["key", "label", "type"],
                "additionalProperties": false
              }
            }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: "roster",
        name: "Roster",
        description: "People profiles — crew, staff or speakers — with fields the tenant defines.",
        allowMultipleInstances: true,
        configJsonSchema: ConfigSchema,
        // A site cannot render admin-defined fields without their definitions,
        // and none of these keys is a secret: they are labels and types.
        publicConfigKeys: ["title", "memberNoun", "fields"],
        permissions:
        [
            new PermissionDefinition("read", "View roster members"),
            new PermissionDefinition("write", "Create and edit roster members"),
        ],
        contentTypes:
        [
            new ContentTypeDefinition(
                Name: "member",
                Fields:
                [
                    new ContentFieldDefinition("name", ContentFieldType.Text, Required: true, "Member name"),
                    new ContentFieldDefinition("role", ContentFieldType.Text, Required: false,
                        "What this member does, e.g. DJ, VJ, Sound engineer. Copied onto a line-up entry when Events links to this roster."),
                    new ContentFieldDefinition("bio", ContentFieldType.RichText, Required: false, "Profile biography"),
                    new ContentFieldDefinition("photo", ContentFieldType.MediaRef, Required: false, "Profile photo",
                        new ContentReferenceTarget(null, null, MediaCategory.Image)),
                    new ContentFieldDefinition("custom", ContentFieldType.Json, Required: false,
                        "Values for the custom fields declared in this instance's config, keyed by field key."),
                ],
                Searchable: true,
                SlugField: "name",
                CustomFields: new CustomFieldsDefinition(ValuesField: "custom", ConfigKey: "fields")),
        ]);

    public void ConfigureServices(IServiceCollection services) { }

    public void MapEndpoints(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapContentList("member", options => options.DefaultPageSize = 50);
        endpoints.MapContentGetBySlug("member");
    }

    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
        => ContentApiFragment.ForListAndGet(instance, "member", Manifest);
}
