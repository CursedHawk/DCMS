using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dcms.PluginSdk.Abstractions.Contracts;

/// <summary>
/// The transport-neutral description of a contract: plain JSON, input and output as JSON
/// Schema. The C# interface is only its in-process binding. This is what the catalog serves,
/// what the TypeScript client and AI tools are generated from, and what a future out-of-process
/// plugin would publish instead of an assembly.
/// </summary>
public sealed record ContractDescriptor(
    string Id,
    string Name,
    int Major,
    string? Description,
    // Providing plugin id; null for a platform (dcms.*) contract.
    string? ProviderPluginId,
    IReadOnlyList<OperationDescriptor> Operations,
    IReadOnlyList<EventDescriptor> Events)
{
    [JsonIgnore] public Type ContractType { get; init; } = null!;

    [JsonIgnore] public bool IsPlatform => ProviderPluginId is null;

    public OperationDescriptor? FindOperation(string name) =>
        Operations.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));
}

public sealed record OperationDescriptor(
    string Name,
    OpRisk Risk,
    string? Permission,
    OpExposure Expose,
    bool ReturnsExternalText,
    string? Description,
    JsonNode InputSchema,
    // Null when the operation returns no value.
    JsonNode? OutputSchema)
{
    [JsonIgnore] public MethodInfo Method { get; init; } = null!;

    /// <summary>The single input record type, or null for an operation that takes none.</summary>
    [JsonIgnore] public Type? InputType { get; init; }

    /// <summary>The <c>Task&lt;T&gt;</c> result type, or null for a plain <c>Task</c>.</summary>
    [JsonIgnore] public Type? OutputType { get; init; }
}

public sealed record EventDescriptor(string Name, JsonNode Schema)
{
    [JsonIgnore] public Type EventType { get; init; } = null!;
}
