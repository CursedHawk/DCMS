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

public sealed record OpenApiPathFragment(
    string RelativePath,                // e.g. "/items/{slug}"
    string Method,                      // get | post | put | delete
    string OperationId,
    string Summary,
    string Description,
    JsonNode? ResponseSchema);
