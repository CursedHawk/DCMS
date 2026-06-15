using System.Text.Json;
using YamlDotNet.Serialization;

namespace Dcms.ContentApi.Delivery;

/// <summary>Converts a JSON string to YAML (for the .yaml spec endpoint).</summary>
public static class YamlConverter
{
    private static readonly ISerializer Serializer = new SerializerBuilder().Build();

    public static string JsonToYaml(string json)
    {
        using var document = JsonDocument.Parse(json);
        var graph = ToObject(document.RootElement);
        return Serializer.Serialize(graph);
    }

    private static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
