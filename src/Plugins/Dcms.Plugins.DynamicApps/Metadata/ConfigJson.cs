using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.DynamicApps.Metadata;

/// <summary>
/// The one serialization of a configuration: camelCase, nulls omitted, object keys sorted,
/// no whitespace. Its SHA-256 is the revision hash, so the same configuration hashes the same
/// on every replica and after a round trip through jsonb (which reorders keys).
/// </summary>
public static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A misspelt property ("requried") is an error, not a silently ignored key: the
        // assistant writes these documents, and a typo it is not told about is a bug it keeps.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static readonly string EmptyHash = Hash(new AppConfig());

    public static JsonObject ToNode(AppConfig config) =>
        JsonSerializer.SerializeToNode(config, Options)!.AsObject();

    /// <exception cref="ContractValidationException">The document is not a valid configuration shape.</exception>
    public static AppConfig FromNode(JsonNode node)
    {
        try
        {
            return node.Deserialize<AppConfig>(Options)
                   ?? throw new ContractValidationException("The configuration is empty.");
        }
        catch (JsonException e)
        {
            throw new ContractValidationException(Describe(e));
        }
    }

    public static AppConfig Parse(string json) => FromNode(JsonNode.Parse(json)!);

    public static string Canonical(AppConfig config) => Sorted(ToNode(config))!.ToJsonString();

    public static string Hash(AppConfig config) => HashOf(Canonical(config));

    public static string HashOf(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    /// <summary>A deep copy with every object's keys in ordinal order; arrays keep theirs.</summary>
    public static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray arr => new JsonArray(arr.Select(Sorted).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    private static string Describe(JsonException e) =>
        e.Path is { Length: > 0 } path ? $"{path}: {e.Message}" : e.Message;
}
