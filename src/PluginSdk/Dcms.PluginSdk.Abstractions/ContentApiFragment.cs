using System.Text.Json.Nodes;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// Builds the standard list + get-by-slug OpenAPI fragment for a content type,
/// injecting the admin-authored instance description into each operation so AI
/// consumers see the intent of this specific instance.
/// </summary>
public static class ContentApiFragment
{
    public static OpenApiFragment ForListAndGet(PluginInstanceContext instance, string contentType, PluginManifest manifest)
    {
        string Describe(string op) => $"{instance.Description}\n\n{op}\n\nPlugin: {manifest.Name} v{manifest.Version}";

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
                ["slug"] = new JsonObject { ["type"] = "string" },
                ["data"] = new JsonObject { ["type"] = "object" },
            },
        };

        return new OpenApiFragment(
            TagName: instance.Name,
            TagDescription: $"{instance.Description}\n\n{manifest.Description}",
            Paths:
            [
                new OpenApiPathFragment($"/{contentType}", "get", $"{instance.Slug}_list_{contentType}",
                    $"List {contentType} items", Describe($"Returns published {contentType} items."), schema),
                new OpenApiPathFragment($"/{contentType}/{{slug}}", "get", $"{instance.Slug}_get_{contentType}",
                    $"Get a {contentType} by slug", Describe($"Returns a single published {contentType} by slug."), schema),
            ],
            Schemas: new Dictionary<string, JsonNode> { [$"{instance.Slug}_{contentType}"] = schema });
    }
}
