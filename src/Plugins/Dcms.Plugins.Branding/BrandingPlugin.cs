using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.Plugins.Branding;

/// <summary>
/// Branding: the tenant's identity — name, tagline, logo, favicon, brand colours —
/// plus open-ended key:value items a site can theme itself with. One instance per
/// tenant.
///
/// The config is split into two sections:
///   • <c>public</c>  — the site's public face. Served to anyone via
///     GET /api/{slug}/branding and the generic GET /api/{slug}/_config, so an
///     externally hosted site can fetch it.
///   • <c>private</c> — tenant-only values (internal keys, integration ids, notes).
///     Stored on the instance config, editable in the admin, but never returned by
///     any public content-api endpoint. A tenant backend that needs them reads the
///     instance config through admin-api with a platform token.
///
/// Only <c>public</c> is listed in <see cref="PluginManifest.PublicConfigKeys"/>,
/// so the private section cannot leak by accident. Like the analytics beacon and
/// form submissions, the SDK does not mount custom plugin routes, so the delivery
/// endpoint lives in content-api (BrandingEndpoints) and serves exactly what this
/// manifest documents.
/// </summary>
public sealed class BrandingPlugin : IPlugin
{
    public const string PluginId = "branding";

    private const string ConfigSchema = """
        {
          "type": "object",
          "properties": {
            "public": {
              "type": "object",
              "title": "Public branding",
              "description": "Served to the published site and to anyone via GET /api/{slug}/branding.",
              "properties": {
                "name": {
                  "type": "string",
                  "title": "Brand name",
                  "description": "The site or organisation name shown in headers, titles and metadata."
                },
                "tagline": {
                  "type": "string",
                  "title": "Tagline",
                  "description": "A short slogan shown alongside the name."
                },
                "logo": {
                  "type": "string",
                  "format": "media",
                  "title": "Logo",
                  "description": "Upload or pick the primary logo (light backgrounds). Stored in your media library and processed automatically."
                },
                "logoDark": {
                  "type": "string",
                  "format": "media",
                  "title": "Dark logo",
                  "description": "Optional logo variant for dark backgrounds. Upload or pick from your media library."
                },
                "favicon": {
                  "type": "string",
                  "format": "media",
                  "title": "Favicon",
                  "description": "Upload or pick the browser tab / bookmark icon. Stored in your media library and processed automatically."
                },
                "primaryColor": {
                  "type": "string",
                  "title": "Primary colour",
                  "description": "Primary brand colour as a CSS hex value, e.g. \"#1d4ed8\".",
                  "pattern": "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$"
                },
                "secondaryColor": {
                  "type": "string",
                  "title": "Secondary colour",
                  "description": "Secondary/accent brand colour as a CSS hex value.",
                  "pattern": "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$"
                },
                "items": {
                  "title": "Public items",
                  "description": "Arbitrary public branding values as key:value pairs (e.g. contact email, social links).",
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "key": {
                        "type": "string",
                        "pattern": "^[A-Za-z][A-Za-z0-9_-]*$"
                      },
                      "value": { "type": "string" }
                    },
                    "required": ["key", "value"],
                    "additionalProperties": false
                  }
                }
              },
              "additionalProperties": false
            },
            "private": {
              "type": "object",
              "title": "Private branding",
              "description": "Tenant-only. Never served on public endpoints; visible only in the admin.",
              "properties": {
                "items": {
                  "title": "Private items",
                  "description": "Internal key:value values (integration ids, notes) kept out of every public response.",
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "key": {
                        "type": "string",
                        "pattern": "^[A-Za-z][A-Za-z0-9_-]*$"
                      },
                      "value": { "type": "string" }
                    },
                    "required": ["key", "value"],
                    "additionalProperties": false
                  }
                }
              },
              "additionalProperties": false
            }
          },
          "additionalProperties": false
        }
        """;

    public PluginManifest Manifest { get; } = PluginManifest.Create(
        id: PluginId,
        name: "Branding",
        description: "Your site's identity — name, tagline, logo, favicon, brand colours and custom key:value items — with a public section read by the site and a tenant-private section kept off public endpoints.",
        allowMultipleInstances: false,
        configJsonSchema: ConfigSchema,
        // Only the public section is world-readable. The private section is
        // deliberately absent, so it can never leak through GET /_config.
        publicConfigKeys: ["public"],
        permissions:
        [
            new PermissionDefinition("read", "View branding"),
            new PermissionDefinition("write", "Configure branding"),
        ]);

    public void ConfigureServices(IServiceCollection services) { }

    // No content types and no custom routes: the delivery endpoint is served by
    // content-api (BrandingEndpoints), which reads the same config this documents.
    public void MapEndpoints(IPluginEndpointBuilder endpoints) { }

    public OpenApiFragment BuildOpenApiFragment(PluginInstanceContext instance)
    {
        var brandingSchema = BrandingResponseSchema();

        return new OpenApiFragment(
            TagName: instance.Name,
            TagDescription: $"{instance.Description}\n\n{Manifest.Description}",
            Paths:
            [
                new OpenApiPathFragment(
                    RelativePath: "/branding",
                    Method: "get",
                    OperationId: $"{instance.Slug}_branding",
                    Summary: "Get public branding",
                    Description:
                        $"{instance.Description}\n\n{Manifest.Description}\n\n" +
                        "Returns the tenant's public branding: name, tagline, logo, favicon, brand " +
                        "colours and any public key:value items. The private branding section is " +
                        "never included. Public — no authentication required.\n\n" +
                        $"Plugin: {Manifest.Name} v{Manifest.Version}",
                    ResponseSchema: Ref($"{instance.Slug}_branding")),
            ],
            Schemas: new Dictionary<string, JsonNode> { [$"{instance.Slug}_branding"] = brandingSchema });
    }

    /// <summary>
    /// The branding declared on an instance, split into its public and private
    /// sections. Shared with content-api so the public endpoint returns exactly
    /// what this manifest documents.
    /// </summary>
    public static BrandingInfo ReadBranding(JsonDocument config)
    {
        var root = config.RootElement;
        var pub = Section(root, "public");
        var priv = Section(root, "private");

        return new BrandingInfo(
            GetString(pub, "name"),
            GetString(pub, "tagline"),
            GetString(pub, "logo"),
            GetString(pub, "logoDark"),
            GetString(pub, "favicon"),
            GetString(pub, "primaryColor"),
            GetString(pub, "secondaryColor"),
            ReadItems(pub),
            ReadItems(priv));
    }

    private static JsonElement? Section(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object &&
           root.TryGetProperty(name, out var section) &&
           section.ValueKind == JsonValueKind.Object
            ? section
            : null;

    private static string? GetString(JsonElement? element, string property)
        => element is { } e && e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Dictionary<string, string> ReadItems(JsonElement? section)
    {
        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        if (section is { } e && e.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("key", out var key) && key.GetString() is { Length: > 0 } k &&
                    item.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                {
                    // Last write wins, matching how a config form serialises the list.
                    items[k] = value.GetString() ?? string.Empty;
                }
            }
        }
        return items;
    }

    private static JsonObject BrandingResponseSchema() => new()
    {
        ["type"] = "object",
        ["description"] = "Public branding. The private section is never returned here.",
        ["properties"] = new JsonObject
        {
            ["name"] = StringProp("The brand / site name."),
            ["tagline"] = StringProp("Short slogan shown alongside the name."),
            ["logoUrl"] = StringProp("Servable URL of the primary logo image (resolved from the uploaded media asset)."),
            ["logoDarkUrl"] = StringProp("Servable URL of the dark-background logo variant, if set."),
            ["faviconUrl"] = StringProp("Servable URL of the favicon (resolved from the uploaded media asset)."),
            ["primaryColor"] = StringProp("Primary brand colour as a CSS hex value."),
            ["secondaryColor"] = StringProp("Secondary brand colour as a CSS hex value."),
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Public custom branding values keyed by their configured key.",
            },
        },
    };

    private static JsonObject StringProp(string description) => new()
    {
        ["type"] = "string",
        ["nullable"] = true,
        ["description"] = description,
    };

    private static JsonObject Ref(string schemaName) => new() { ["$ref"] = $"#/components/schemas/{schemaName}" };
}

/// <summary>
/// Branding resolved from an instance. The logo/favicon fields hold media asset
/// ids (uploaded through the standard media pipeline and stored in tenant media);
/// the delivery endpoint resolves them to servable URLs via <c>IMediaResolver</c>.
/// <see cref="Items"/> is the public section (served on GET /branding);
/// <see cref="PrivateItems"/> is the tenant-private section and must never be
/// written to a public response.
/// </summary>
public sealed record BrandingInfo(
    string? Name,
    string? Tagline,
    string? LogoAssetId,
    string? LogoDarkAssetId,
    string? FaviconAssetId,
    string? PrimaryColor,
    string? SecondaryColor,
    IReadOnlyDictionary<string, string> Items,
    IReadOnlyDictionary<string, string> PrivateItems);
