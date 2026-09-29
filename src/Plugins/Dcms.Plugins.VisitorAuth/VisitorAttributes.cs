using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Plugins.VisitorAuth.Api;

namespace Dcms.Plugins.VisitorAuth;

/// <summary>Reading and validating attribute definitions and values.</summary>
public static class VisitorAttributes
{
    public const string ConfigKey = "attributes";
    private const int MaxTextLength = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The definitions in an instance config; malformed entries are skipped, never fatal.</summary>
    public static IReadOnlyList<AttributeDefinition> Read(JsonDocument config)
    {
        if (config.RootElement.ValueKind != JsonValueKind.Object
            || !config.RootElement.TryGetProperty(ConfigKey, out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new List<AttributeDefinition>();
        foreach (var item in list.EnumerateArray())
        {
            try
            {
                if (item.Deserialize<AttributeDefinition>(Json) is { Key.Length: > 0 } definition
                    && result.All(d => d.Key != definition.Key))
                {
                    result.Add(definition);
                }
            }
            catch (JsonException)
            {
                // A definition the admin form could not have produced; ignore it rather than
                // taking the whole profile feature down over one entry.
            }
        }
        return result;
    }

    public static Dictionary<string, JsonElement> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(string.IsNullOrWhiteSpace(json) ? "{}" : json)
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Values whose definition is at least <paramref name="minimum"/> visible. Undefined keys never pass.</summary>
    public static Dictionary<string, JsonElement> Visible(
        IReadOnlyDictionary<string, JsonElement> values, IReadOnlyList<AttributeDefinition> definitions, AttributeVisibility minimum) =>
        definitions
            .Where(d => d.Visibility >= minimum && values.ContainsKey(d.Key))
            .ToDictionary(d => d.Key, d => values[d.Key]);

    /// <summary>
    /// Applies <paramref name="changes"/> to <paramref name="current"/>: each key must be defined
    /// and <paramref name="allowed"/>, each value must match its type; a JSON null removes it.
    /// </summary>
    /// <exception cref="ContractValidationException">With every problem found, one per line.</exception>
    public static Dictionary<string, JsonElement> Apply(
        IReadOnlyDictionary<string, JsonElement> current,
        IReadOnlyDictionary<string, JsonElement> changes,
        IReadOnlyList<AttributeDefinition> definitions,
        Func<AttributeDefinition, bool> allowed,
        out List<string> changedKeys)
    {
        var next = new Dictionary<string, JsonElement>(current);
        var problems = new List<string>();
        changedKeys = [];
        foreach (var (key, value) in changes)
        {
            var definition = definitions.FirstOrDefault(d => d.Key == key);
            if (definition is null)
            {
                problems.Add($"'{key}' is not a defined attribute.");
                continue;
            }
            if (!allowed(definition))
            {
                problems.Add($"'{key}' cannot be changed here.");
                continue;
            }
            if (value.ValueKind == JsonValueKind.Null)
            {
                if (next.Remove(key))
                {
                    changedKeys.Add(key);
                }
                continue;
            }
            if (Problem(definition, value) is { } problem)
            {
                problems.Add($"'{key}' {problem}");
                continue;
            }
            next[key] = value.Clone();
            changedKeys.Add(key);
        }
        if (problems.Count > 0)
        {
            throw new ContractValidationException(string.Join("\n", problems));
        }
        return next;
    }

    public static string Serialize(IReadOnlyDictionary<string, JsonElement> values) => JsonSerializer.Serialize(values);

    private static string? Problem(AttributeDefinition definition, JsonElement value) => definition.Type switch
    {
        AttributeType.Text when value.ValueKind != JsonValueKind.String => "must be text.",
        AttributeType.Text when value.GetString()!.Length > MaxTextLength => $"must be at most {MaxTextLength} characters.",
        AttributeType.Number when value.ValueKind != JsonValueKind.Number => "must be a number.",
        AttributeType.Boolean when value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) => "must be true or false.",
        AttributeType.Date when value.ValueKind != JsonValueKind.String
            || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            => "must be a date (yyyy-MM-dd).",
        AttributeType.Select when value.ValueKind != JsonValueKind.String
            || !(definition.Options ?? []).Contains(value.GetString()!)
            => "must be one of the defined options.",
        _ => null,
    };

    /// <summary>The config JSON Schema fragment for the admin form.</summary>
    public static JsonObject ConfigSchema() => new()
    {
        ["type"] = "array",
        ["title"] = "Profile attributes",
        ["description"] = "Fields on each visitor's profile. Visibility decides who besides the visitor and admins may read a value.",
        ["items"] = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("key", "label"),
            ["properties"] = new JsonObject
            {
                ["key"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[a-z][a-zA-Z0-9]{0,63}$", ["title"] = "Key" },
                ["label"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128, ["title"] = "Label" },
                ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("text", "number", "boolean", "date", "select"), ["default"] = "text" },
                ["visibility"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("private", "plugins", "public"), ["default"] = "private" },
                ["visitorEditable"] = new JsonObject { ["type"] = "boolean", ["default"] = true, ["title"] = "Visitor can edit" },
                ["options"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["title"] = "Options (select only)" },
            },
        },
        ["maxItems"] = 50,
    };
}
