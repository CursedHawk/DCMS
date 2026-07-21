using System.Linq;
using System.Text.Json.Nodes;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// Builds the standard list + get-by-slug OpenAPI fragment for a content type,
/// injecting the admin-authored instance description into each operation so AI
/// consumers see the intent of this specific instance. The item schema is typed
/// from the content type's field definitions (see <see cref="ContentFieldSchema"/>),
/// and the list operation returns the paged envelope the delivery API actually emits.
/// </summary>
public static class ContentApiFragment
{
    public static OpenApiFragment ForListAndGet(PluginInstanceContext instance, string contentType, PluginManifest manifest)
    {
        string Describe(string op) => $"{instance.Description}\n\n{op}\n\nPlugin: {manifest.Name} v{manifest.Version}";

        var definition = manifest.ContentTypes.FirstOrDefault(c => c.Name == contentType);
        var dataSchema = ContentFieldSchema.BuildDataObject(definition);

        var itemSchemaName = $"{instance.Slug}_{contentType}";
        var listSchemaName = $"{instance.Slug}_{contentType}_list";

        // A published content item — mirrors ContentItemDto from the delivery API:
        // platform-managed envelope fields plus the typed field data.
        var itemSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
                ["pluginInstanceId"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
                ["contentType"] = new JsonObject { ["type"] = "string" },
                ["slug"] = new JsonObject { ["type"] = "string" },
                ["versionNo"] = new JsonObject { ["type"] = "integer" },
                ["data"] = dataSchema,
                ["publishedAt"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            },
            ["required"] = new JsonArray { "id", "pluginInstanceId", "contentType", "slug", "versionNo", "data", "publishedAt" },
        };

        // The list endpoint returns { items, page, pageSize, totalCount }, not a bare item.
        var listSchema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = Ref(itemSchemaName),
                },
                ["page"] = new JsonObject { ["type"] = "integer" },
                ["pageSize"] = new JsonObject { ["type"] = "integer" },
                ["totalCount"] = new JsonObject { ["type"] = "integer" },
            },
            ["required"] = new JsonArray { "items", "page", "pageSize", "totalCount" },
        };

        return new OpenApiFragment(
            TagName: instance.Name,
            TagDescription: $"{instance.Description}\n\n{manifest.Description}",
            Paths:
            [
                new OpenApiPathFragment($"/{contentType}", "get", $"{instance.Slug}_list_{contentType}",
                    $"List {contentType} items", Describe($"Returns published {contentType} items."),
                    Ref(listSchemaName),
                    Parameters: [PageParam(), PageSizeParam()]),
                new OpenApiPathFragment($"/{contentType}/{{slug}}", "get", $"{instance.Slug}_get_{contentType}",
                    $"Get a {contentType} by slug", Describe($"Returns a single published {contentType} by slug."),
                    Ref(itemSchemaName),
                    Parameters: [SlugParam()]),
            ],
            Schemas: new Dictionary<string, JsonNode>
            {
                [itemSchemaName] = itemSchema,
                [listSchemaName] = listSchema,
            });
    }

    private static JsonObject Ref(string schemaName)
        => new() { ["$ref"] = $"#/components/schemas/{schemaName}" };

    private static JsonObject PageParam() => new()
    {
        ["name"] = "page",
        ["in"] = "query",
        ["required"] = false,
        ["description"] = "1-based page number.",
        ["schema"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["default"] = 1 },
    };

    private static JsonObject PageSizeParam() => new()
    {
        ["name"] = "pageSize",
        ["in"] = "query",
        ["required"] = false,
        ["description"] = "Items per page (server clamps to its configured maximum).",
        ["schema"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
    };

    private static JsonObject SlugParam() => new()
    {
        ["name"] = "slug",
        ["in"] = "path",
        ["required"] = true,
        ["description"] = "The item's URL slug.",
        ["schema"] = new JsonObject { ["type"] = "string" },
    };
}
