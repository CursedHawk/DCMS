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
    IReadOnlyList<OperationDescriptor> Operations,
    IReadOnlyList<EventDescriptor> Events)
{
    [JsonIgnore] public Type ContractType { get; init; } = null!;

    /// <summary>A <c>dcms.*</c> contract, implemented by the host for every plugin.</summary>
    [JsonIgnore] public bool IsPlatform => ContractIds.IsPlatform(Id);

    /// <summary>The .NET interface, for C# consumers: what to reference and resolve.</summary>
    public string ClrType => ContractType.FullName ?? ContractType.Name;

    /// <summary>The assembly (and NuGet package) the interface ships in.</summary>
    public string Assembly => ContractType.Assembly.GetName().Name ?? string.Empty;

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
