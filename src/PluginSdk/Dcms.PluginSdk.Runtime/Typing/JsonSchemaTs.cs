using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dcms.PluginSdk.Runtime.Typing;

/// <summary>
/// JSON Schema → TypeScript, for every generator that types a schema: the tenant site client
/// (admin-api's emitter), the Functions SDK, and the realtime channels (ADR 0037).
///
/// <para>Covers what contract records, plugin fragments and channel payloads produce:
/// <c>type</c> (one or several, <c>null</c> included) and <c>nullable</c>, <c>const</c>, an
/// <c>enum</c> of any literals, <c>oneOf</c>/<c>anyOf</c> as unions, <c>allOf</c> as an
/// intersection, objects with properties (described properties keep their description as a doc
/// comment), maps from <c>additionalProperties</c>, arrays, and boolean schemas. <c>$ref</c> and
/// any other host-specific shape go through <paramref name="special"/>, which sees every node
/// first. Anything else, and anything deeper than <paramref name="maxDepth"/>, is
/// <paramref name="unknown"/>: a generated type guides, it never lies.</para>
/// </summary>
/// <param name="unknown">The type of what cannot be expressed (<c>unknown</c>, or the Functions SDK's <c>Json</c>).</param>
/// <param name="special">Answers a node itself (a <c>$ref</c> to a named type, a branded string) or returns null.</param>
public sealed class JsonSchemaTs(string unknown = "unknown", Func<JsonObject, string?>? special = null, int maxDepth = 8)
{
    public string Type(JsonNode? schema) => Render(schema, 0);

    /// <summary>The members of an object schema, one per line, for an <c>interface</c> body.</summary>
    public string Members(JsonObject schema, string indent = "  ")
    {
        var required = Required(schema);
        var lines = (schema["properties"] as JsonObject ?? []).Select(p =>
            $"{Doc(p.Value as JsonObject, indent)}{indent}{Key(p.Key)}{(required.Contains(p.Key) ? "" : "?")}: {Render(p.Value, 1)};\n");
        return string.Concat(lines);
    }

    private string Render(JsonNode? node, int depth)
    {
        if (node is JsonValue boolean && boolean.TryGetValue<bool>(out var accepts))
        {
            return accepts ? unknown : "never";
        }
        if (node is not JsonObject s || depth > maxDepth)
        {
            return unknown;
        }
        var ts = special?.Invoke(s) ?? s switch
        {
            _ when s.ContainsKey("const") => Literal(s["const"]),
            _ when s["enum"] is JsonArray values && values.Count > 0 => Union(values.Select(Literal)),
            _ when (s["oneOf"] ?? s["anyOf"]) is JsonArray any && any.Count > 0 => Union(any.Select(a => Render(a, depth + 1))),
            _ when s["allOf"] is JsonArray all && all.Count > 0 => string.Join(" & ", all.Select(a => Group(Render(a, depth + 1))).Distinct()),
            _ => ByType(s, depth),
        };
        return s["nullable"] is JsonValue n && n.TryGetValue<bool>(out var nullable) && nullable && ts != unknown && !ts.EndsWith("| null")
            ? $"{ts} | null"
            : ts;
    }

    private string ByType(JsonObject s, int depth)
    {
        var types = s["type"] switch
        {
            JsonArray many => many.Select(t => (t as JsonValue)?.GetValue<string>()).OfType<string>().ToList(),
            JsonValue one when one.TryGetValue<string>(out var t) => [t],
            _ => s.ContainsKey("properties") ? ["object"] : s.ContainsKey("items") ? ["array"] : [],
        };
        if (types.Count == 0)
        {
            return unknown;
        }
        return Union(types.Select(type => type switch
        {
            "null" => "null",
            "string" => "string",
            "integer" or "number" => "number",
            "boolean" => "boolean",
            "array" => ArrayOf(Render(s["items"], depth + 1)),
            "object" => ObjectOf(s, depth),
            _ => unknown,
        }));
    }

    private string ObjectOf(JsonObject s, int depth)
    {
        if (s["properties"] is JsonObject props && props.Count > 0)
        {
            var required = Required(s);
            return "{ " + string.Join("; ", props.Select(p =>
                $"{Key(p.Key)}{(required.Contains(p.Key) ? "" : "?")}: {Render(p.Value, depth + 1)}")) + " }";
        }
        return s["additionalProperties"] is JsonObject values
            ? $"Record<string, {Render(values, depth + 1)}>"
            : $"Record<string, {unknown}>";
    }

    private static HashSet<string> Required(JsonObject s) =>
        (s["required"] as JsonArray)?.Select(r => (r as JsonValue)?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];

    private string Union(IEnumerable<string> parts)
    {
        var distinct = parts.Distinct().ToList();
        // unknown absorbs everything it is united with; a union with it says nothing more.
        return distinct.Contains(unknown) ? unknown : string.Join(" | ", distinct);
    }

    private static string Group(string type) => type.Contains(' ') && !type.StartsWith('{') ? $"({type})" : type;

    private static string ArrayOf(string element) => element.Contains(' ') && !element.StartsWith('{') ? $"({element})[]" : $"{element}[]";

    private static string Literal(JsonNode? value) => value switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<string>(out var text) => JsonSerializer.Serialize(text),
        JsonValue v => v.ToJsonString(),
        _ => "never",
    };

    /// <summary>A property name, quoted unless it is a plain identifier.</summary>
    public static string Key(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
            ? name
            : JsonSerializer.Serialize(name);

    /// <summary>A doc comment line for a described schema, or nothing.</summary>
    public static string Doc(JsonObject? schema, string indent = "  ") =>
        schema?["description"] is JsonValue d && d.TryGetValue<string>(out var text) && text.Length > 0
            ? $"{indent}/** {Comment(text)} */\n"
            : "";

    /// <summary>Text safe inside a one-line comment: no line breaks, and nothing that closes it.</summary>
    public static string Comment(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Replace("*/", "* /", StringComparison.Ordinal);
}
