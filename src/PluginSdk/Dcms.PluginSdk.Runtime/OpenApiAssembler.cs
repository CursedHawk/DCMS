using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;

namespace Dcms.PluginSdk.Runtime;

/// <summary>
/// Assembles the per-tenant OpenAPI document from the enabled plugin instances'
/// fragments. Built directly as a JSON object (OpenAPI 3.1) to avoid coupling to
/// a specific OpenAPI object-model library. Paths are mounted under
/// /api/{instanceSlug}; tags and component schemas are namespaced per instance.
/// </summary>
public sealed class OpenApiAssembler(PluginRegistry registry)
{
    private const string Preamble =
        "Auto-generated content API for this tenant. Each tag is a configured plugin instance; " +
        "its description states what that instance is for. List endpoints return " +
        "{ items, page, pageSize, totalCount } and accept ?page & ?pageSize. Media fields hold asset " +
        "ids served at /api/media/{assetId}/{variant} (images: webp-320..1920, thumb; video: hls-master).";

    /// <param name="serverUrls">
    /// Base URLs to advertise as OpenAPI <c>servers</c> (e.g. the tenant's verified
    /// domains), so a docs "try it" call targets a real host. When null/empty the
    /// spec uses a relative <c>"/"</c> server (correct when served from that host).
    /// </param>
    /// <param name="tagging">
    /// Whether this tenant actually publishes tags. When it does, the document
    /// gains the tag index and a <c>?tag=</c> filter on every list; when it does
    /// not, neither appears — a documented endpoint that answers with an empty
    /// list on every site that has never used a tag is noise in every one of
    /// those tenants' API docs, and the reason this is a parameter rather than
    /// something always emitted.
    /// </param>
    public JsonObject Build(
        string tenantName,
        IReadOnlyList<PluginInstanceContext> instances,
        IReadOnlyList<string>? serverUrls = null,
        bool tagging = false)
    {
        var paths = new JsonObject();
        var tags = new JsonArray();
        var schemas = new JsonObject();

        foreach (var instance in instances.OrderBy(i => i.Slug, StringComparer.Ordinal))
        {
            var plugin = registry.FindPlugin(instance.PluginId);
            if (plugin is null)
            {
                continue;
            }

            var fragment = plugin.BuildOpenApiFragment(instance);
            tags.Add(new JsonObject { ["name"] = fragment.TagName, ["description"] = fragment.TagDescription });

            foreach (var path in fragment.Paths)
            {
                var fullPath = $"/api/{instance.Slug}{path.RelativePath}";
                var pathItem = paths[fullPath] as JsonObject ?? [];
                pathItem[path.Method] = BuildOperation(fragment.TagName, path, tagging);
                paths[fullPath] = pathItem;
            }

            foreach (var (name, schema) in fragment.Schemas)
            {
                schemas[name] = schema.DeepClone();
            }
        }

        if (tagging)
        {
            tags.Add(new JsonObject
            {
                ["name"] = TagIndexTag,
                ["description"] =
                    "Tags this tenant's published content carries. Tags are the one piece of " +
                    "structure that crosses collections — the same tag can sit on an event, a " +
                    "gallery and a post — so they are indexed here rather than per plugin.",
            });
            paths["/api/tags"] = new JsonObject { ["get"] = TagIndexOperation() };
        }

        var servers = new JsonArray();
        if (serverUrls is { Count: > 0 })
        {
            foreach (var url in serverUrls)
            {
                servers.Add(new JsonObject { ["url"] = url });
            }
        }
        else
        {
            servers.Add(new JsonObject { ["url"] = "/" });
        }

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = $"{tenantName} Content API",
                ["version"] = "1.0.0",
                ["description"] = Preamble,
            },
            ["servers"] = servers,
            ["tags"] = tags,
            ["paths"] = paths,
            ["components"] = new JsonObject { ["schemas"] = schemas },
        };
    }

    /// <summary>The OpenAPI tag the cross-collection tag index is grouped under.</summary>
    private const string TagIndexTag = "Tags";

    /// <summary>
    /// A list operation is a GET whose path names no single item. That is the
    /// only shape `?tag=` means anything on — filtering a fetch-by-slug would be
    /// a parameter that can only ever return the item or nothing.
    /// </summary>
    private static bool IsListOperation(OpenApiPathFragment path) =>
        string.Equals(path.Method, "get", StringComparison.OrdinalIgnoreCase)
        && !path.RelativePath.Contains('{');

    private static JsonObject BuildOperation(string tag, OpenApiPathFragment path, bool tagging)
    {
        var operation = new JsonObject
        {
            ["tags"] = new JsonArray { tag },
            ["operationId"] = path.OperationId,
            ["summary"] = path.Summary,
            ["description"] = path.Description,
        };

        var parameters = new JsonArray();
        if (path.Parameters is { Count: > 0 })
        {
            foreach (var parameter in path.Parameters)
            {
                parameters.Add(parameter.DeepClone());
            }
        }
        if (tagging && IsListOperation(path))
        {
            parameters.Add(QueryParameter(
                "tag",
                "Return only items carrying this tag. Matched case-insensitively across " +
                "every tag field of the item. See GET /api/tags for what this tenant uses."));
            parameters.Add(QueryParameter(
                "tagField",
                "Restrict the tag match to one field, for content types that carry more " +
                "than one tag list (an event has genres as well as tags). Optional; " +
                "omitted means any field."));
        }
        if (parameters.Count > 0)
        {
            operation["parameters"] = parameters;
        }

        if (path.RequestBodySchema is { } requestSchema)
        {
            operation["requestBody"] = new JsonObject
            {
                ["required"] = true,
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject { ["schema"] = requestSchema.DeepClone() },
                },
            };
        }

        // The success response carries a JSON body only when a schema is supplied
        // (reads); write beacons like analytics collect return an empty 202.
        var response = new JsonObject { ["description"] = "Success" };
        if (path.ResponseSchema is { } responseSchema)
        {
            response["content"] = new JsonObject
            {
                ["application/json"] = new JsonObject { ["schema"] = responseSchema.DeepClone() },
            };
        }
        operation["responses"] = new JsonObject { [path.SuccessStatus] = response };

        return operation;
    }

    private static JsonObject QueryParameter(string name, string description) => new()
    {
        ["name"] = name,
        ["in"] = "query",
        ["required"] = false,
        ["description"] = description,
        ["schema"] = new JsonObject { ["type"] = "string" },
    };

    /// <summary>
    /// `GET /api/tags`, described in full — the point of the index is that a site
    /// can be built against it without reading this service's source, so each
    /// occurrence carries the call that fetches it.
    /// </summary>
    private static JsonObject TagIndexOperation() => new()
    {
        ["tags"] = new JsonArray { TagIndexTag },
        ["operationId"] = "listTags",
        ["summary"] = "Tags in use, and where",
        ["description"] =
            "Every tag this tenant's published content carries, most used first, each with the " +
            "collections and fields it appears in and a ready-made delivery call for that " +
            "combination. Tags differing only in case are grouped and reported under the " +
            "spelling used most. Drafts are excluded — an unpublished tag is as unpublished as " +
            "the item carrying it.",
        ["parameters"] = new JsonArray
        {
            QueryParameter("contentType", "Only tags used by this content type."),
            QueryParameter("field", "Only tags coming from this field (e.g. `genres`)."),
        },
        ["responses"] = new JsonObject
        {
            ["200"] = new JsonObject
            {
                ["description"] = "The tag index",
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject { ["schema"] = TagIndexSchema() },
                },
            },
        },
    };

    private static JsonObject TagIndexSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["totalCount"] = new JsonObject { ["type"] = "integer", ["description"] = "How many distinct tags." },
            ["items"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["tag"] = new JsonObject { ["type"] = "string" },
                        ["count"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Published items carrying it, across every collection.",
                        },
                        ["occurrences"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "Where the tag is used, most used first.",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["instance"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Plugin instance slug — the first path segment of its delivery URL.",
                                    },
                                    ["contentType"] = new JsonObject { ["type"] = "string" },
                                    ["field"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "The content field the tag came from.",
                                    },
                                    ["count"] = new JsonObject { ["type"] = "integer" },
                                    ["url"] = new JsonObject
                                    {
                                        ["type"] = "string",
                                        ["description"] = "Delivery call returning exactly these items.",
                                    },
                                },
                            },
                        },
                    },
                },
            },
        },
    };
}
