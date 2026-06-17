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
    public JsonObject Build(
        string tenantName,
        IReadOnlyList<PluginInstanceContext> instances,
        IReadOnlyList<string>? serverUrls = null)
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
                pathItem[path.Method] = BuildOperation(fragment.TagName, path);
                paths[fullPath] = pathItem;
            }

            foreach (var (name, schema) in fragment.Schemas)
            {
                schemas[name] = schema.DeepClone();
            }
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

    private static JsonObject BuildOperation(string tag, OpenApiPathFragment path)
    {
        var operation = new JsonObject
        {
            ["tags"] = new JsonArray { tag },
            ["operationId"] = path.OperationId,
            ["summary"] = path.Summary,
            ["description"] = path.Description,
        };

        if (path.Parameters is { Count: > 0 })
        {
            var parameters = new JsonArray();
            foreach (var parameter in path.Parameters)
            {
                parameters.Add(parameter.DeepClone());
            }
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
}
