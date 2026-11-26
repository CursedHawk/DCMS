using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dcms.Plugins.Functions.Build;

/// <summary>
/// JSON Schema → C# records, for the typed contract operations of Sdk.g.cs (ADR 0030, 0037).
/// An object with properties becomes a sealed record with <c>init</c> properties (<c>required</c>
/// where the schema says so, nullable otherwise), named from the hint and kept unique against
/// <paramref name="names"/>; a <c>const</c> property is a fixed getter, so it is always sent and
/// never set wrong. Arrays are <c>IReadOnlyList</c>, maps <c>IReadOnlyDictionary</c>, integers
/// <c>long</c>, numbers <c>double</c>; enums stay <c>string</c> with the values in the doc
/// comment. A union, several types, or anything deeper than six levels is <c>JsonNode</c>.
/// The records are inserted at the start of <paramref name="types"/>, as SdkTypings does.
/// </summary>
public sealed class JsonSchemaCs(HashSet<string> names, StringBuilder types)
{
    private const int MaxDepth = 6;

    /// <summary>The C# type of <paramref name="schema"/>, never nullable-annotated (the caller decides).</summary>
    public string Type(JsonNode? schema, string hint) => Render(schema, hint, 0);

    /// <summary>The branches of a union of objects, each typed as a record; null when the schema is not one.</summary>
    public IReadOnlyList<string>? Branches(JsonNode? schema, string hint)
    {
        if (schema is not JsonObject s || (s["oneOf"] ?? s["anyOf"]) is not JsonArray any || any.Count == 0
            || any.Any(b => b is not JsonObject o || o["properties"] is not JsonObject))
        {
            return null;
        }
        return any.Select((branch, i) => Render(branch, hint + BranchName(branch!.AsObject(), i), 0)).ToList();
    }

    private string Render(JsonNode? node, string hint, int depth)
    {
        if (node is not JsonObject s || depth > MaxDepth || s.ContainsKey("oneOf") || s.ContainsKey("anyOf") || s.ContainsKey("allOf"))
        {
            return "JsonNode";
        }
        if (s["const"] is JsonValue c)
        {
            return Scalar(c);
        }
        if (s["enum"] is JsonArray values && values.Count > 0)
        {
            return values.All(v => v is JsonValue sv && sv.TryGetValue<string>(out _)) ? "string" : "JsonNode";
        }
        var types = s["type"] switch
        {
            JsonArray many => many.Select(t => (t as JsonValue)?.GetValue<string>()).OfType<string>().Where(t => t != "null").Distinct().ToList(),
            JsonValue one when one.TryGetValue<string>(out var t) => [t],
            _ => s.ContainsKey("properties") ? ["object"] : [],
        };
        if (types.Count != 1)
        {
            return "JsonNode";
        }
        return types[0] switch
        {
            "string" => "string",
            "integer" => "long",
            "number" => "double",
            "boolean" => "bool",
            "array" => $"IReadOnlyList<{Nullable(Render(s["items"], hint + "Item", depth + 1))}>",
            "object" when s["properties"] is JsonObject props && props.Count > 0 => Record(s, props, hint, depth),
            "object" when s["additionalProperties"] is JsonObject values2 => $"IReadOnlyDictionary<string, {Nullable(Render(values2, hint + "Value", depth + 1))}>",
            _ => "JsonNode",
        };
    }

    private string Record(JsonObject schema, JsonObject props, string hint, int depth)
    {
        var name = Unique(names, Pascal(hint));
        var required = (schema["required"] as JsonArray)?.Select(r => (r as JsonValue)?.GetValue<string>()).OfType<string>().ToHashSet() ?? [];
        var text = new StringBuilder();
        if (Description(schema) is { } summary) text.Append($"/// <summary>{Xml(summary)}</summary>\n");
        text.Append($"public sealed record {name}\n{{\n");
        var members = new HashSet<string>(StringComparer.Ordinal) { name, "EqualityContract" };
        foreach (var (key, value) in props)
        {
            var member = Unique(members, Pascal(key));
            var prop = value as JsonObject;
            var doc = Description(prop) + (prop?["enum"] is JsonArray e ? " One of: " + string.Join(", ", e.Select(v => v?.ToJsonString())) : "");
            if (doc.Trim().Length > 0) text.Append($"    /// <summary>{Xml(doc.Trim())}</summary>\n");
            var attribute = $"[System.Text.Json.Serialization.JsonPropertyName({JsonSerializer.Serialize(key)})]";
            if (prop?["const"] is JsonValue constant)
            {
                text.Append($"    {attribute} public {Scalar(constant)} {member} => {constant.ToJsonString()};\n");
                continue;
            }
            var type = Render(value, name + Pascal(key), depth + 1);
            text.Append(required.Contains(key)
                ? $"    {attribute} public required {type} {member} {{ get; init; }}\n"
                : $"    {attribute} public {Nullable(type)} {member} {{ get; init; }}\n");
        }
        types.Insert(0, text.Append("}\n\n").ToString());
        return name;
    }

    private static string BranchName(JsonObject branch, int index) =>
        (branch["properties"] as JsonObject)?.Select(p => p.Value?["const"]).OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var text) ? text : null).FirstOrDefault(t => t is not null) is { } discriminator
            ? Pascal(discriminator)
            : $"Variant{index + 1}";

    private static string Scalar(JsonValue value) =>
        value.TryGetValue<string>(out _) ? "string" : value.TryGetValue<bool>(out _) ? "bool" : value.TryGetValue<long>(out _) ? "long" : "double";

    private static string Nullable(string type) => type.EndsWith('?') ? type : type + "?";

    private static string? Description(JsonObject? schema) =>
        schema?["description"] is JsonValue d && d.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static string Xml(string text) =>
        System.Security.SecurityElement.Escape(string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))) ?? "";

    private static string Unique(HashSet<string> taken, string name)
    {
        var candidate = name;
        for (var i = 2; !taken.Add(candidate); i++) candidate = name + i;
        return candidate;
    }

    private static string Pascal(string name)
    {
        var pascal = string.Concat(name.Split(['-', '_', '.', ' ', '/', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..])
            .Select(p => string.Concat(p.Where(c => char.IsLetterOrDigit(c) || c == '_'))));
        return pascal.Length > 0 && char.IsLetter(pascal[0]) ? pascal : "T" + pascal;
    }
}
