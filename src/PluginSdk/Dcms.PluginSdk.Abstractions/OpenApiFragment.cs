using System.Text.Json.Nodes;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// Plugin-contributed slice of the per-tenant OpenAPI document. Paths are
/// relative to /api/{instanceSlug}; the assembler (PluginSdk.Runtime) mounts
/// them, namespaces schemas as {InstanceSlug}_{SchemaName}, and renders every
/// operation description as:
/// "{instance.Description}\n\n{operation.Description}\n\nPlugin: {Name} v{Version}".
/// </summary>
public sealed record OpenApiFragment(
    string TagName,
    string TagDescription,
    IReadOnlyList<OpenApiPathFragment> Paths,
    IReadOnlyDictionary<string, JsonNode> Schemas)
{
    public static readonly OpenApiFragment Empty = new(string.Empty, string.Empty, [], new Dictionary<string, JsonNode>());
}

/// <param name="ResponseSchema">Success-response body schema. When null the success response carries no body.</param>
/// <param name="RequestBodySchema">JSON request-body schema for write operations (POST/PUT). Null for reads.</param>
/// <param name="Parameters">Raw OpenAPI parameter objects (e.g. a required header). Null when none.</param>
/// <param name="SuccessStatus">HTTP status of the success response (e.g. "200", "202").</param>
public sealed record OpenApiPathFragment(
    string RelativePath,                // e.g. "/items/{slug}"
    string Method,                      // get | post | put | delete
    string OperationId,
    string Summary,
    string Description,
    JsonNode? ResponseSchema,
    JsonNode? RequestBodySchema = null,
    IReadOnlyList<JsonNode>? Parameters = null,
    string SuccessStatus = "200");
