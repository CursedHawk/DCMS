using System.Globalization;
using System.Text.Json.Nodes;

namespace Dcms.Plugins.Functions.Http;

/// <summary>
/// An HTTP call's input against the function's input schema (ADR 0029), before a sandbox starts:
/// what does not fit is refused with 400 and what is wrong. It knows the keywords the build
/// writes (<see cref="Build.FunctionManifest.Schema"/>), and nothing a schema does not say is refused.
/// </summary>
public static class InputCheck
{
    private const int MaxProblems = 10;

    /// <summary>What does not fit, as "path: what was expected"; empty when it fits.</summary>
    public static List<string> Problems(JsonNode? value, JsonObject? schema)
    {
        var problems = new List<string>();
        if (schema is { Count: > 0 }) Check(value, schema, "input", problems);
        return problems;
    }

    /// <summary>
    /// A query string as the input it stands for: each value as the type its property says
    /// (<c>?count=3&amp;open=true</c> is <c>{"count": 3, "open": true}</c>), else as text.
    /// </summary>
    public static JsonObject FromQuery(IReadOnlyDictionary<string, string> query, JsonObject? schema)
    {
        var properties = schema?["properties"] as JsonObject;
        var input = new JsonObject();
        foreach (var (key, text) in query)
        {
            input[key] = Typed(text, Types(properties?[key] as JsonObject));
        }
        return input;
    }

    private static JsonNode? Typed(string text, IReadOnlyList<string> types)
    {
        // Parsed, not created from .NET values: a node is then read back the same way a body's is.
        if (types.Contains("integer") && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
            return JsonNode.Parse(whole.ToString(CultureInfo.InvariantCulture));
        if (types.Contains("number") && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
            return JsonNode.Parse(number.ToString("R", CultureInfo.InvariantCulture));
        if (types.Contains("boolean") && bool.TryParse(text, out var flag)) return JsonNode.Parse(flag ? "true" : "false");
        if (types.Contains("null") && text.Length == 0) return null;
        return text;
    }

    private static IReadOnlyList<string> Types(JsonObject? schema) => schema?["type"] switch
    {
        JsonValue one when one.TryGetValue<string>(out var t) => [t],
        JsonArray many => many.Select(t => t is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToList(),
        _ => [],
    };

    private static void Check(JsonNode? value, JsonObject schema, string path, List<string> problems)
    {
        if (problems.Count >= MaxProblems) return;

        if (schema["anyOf"] is JsonArray anyOf)
        {
            var fits = anyOf.OfType<JsonObject>().Any(option =>
            {
                var found = new List<string>();
                Check(value, option, path, found);
                return found.Count == 0;
            });
            if (!fits) problems.Add($"{path}: does not fit any of the shapes it may have.");
            return;
        }

        var types = Types(schema);
        if (types.Count > 0 && !types.Any(t => Is(value, t)))
        {
            problems.Add($"{path}: expected {string.Join(" or ", types)}, got {Kind(value)}.");
            return;
        }
        if (schema["enum"] is JsonArray allowed && !allowed.Any(a => JsonNode.DeepEquals(a, value)))
        {
            problems.Add($"{path}: expected one of {string.Join(", ", allowed.Select(a => a?.ToJsonString() ?? "null"))}.");
            return;
        }
        if (value is JsonValue text && text.TryGetValue<string>(out var s) && schema["format"] is JsonValue format && !Formatted(s, format.GetValue<string>()))
        {
            problems.Add($"{path}: expected a {format.GetValue<string>()}.");
            return;
        }

        if (value is JsonObject obj)
        {
            foreach (var name in (schema["required"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>())
            {
                if (!obj.ContainsKey(name)) problems.Add($"{path}.{name}: is required.");
            }
            var properties = schema["properties"] as JsonObject;
            var others = schema["additionalProperties"] as JsonObject;
            foreach (var (name, child) in obj)
            {
                if (properties?[name] is JsonObject property) Check(child, property, $"{path}.{name}", problems);
                else if (others is { Count: > 0 }) Check(child, others, $"{path}.{name}", problems);
            }
        }
        else if (value is JsonArray array && schema["items"] is JsonObject { Count: > 0 } items)
        {
            for (var i = 0; i < array.Count && problems.Count < MaxProblems; i++)
            {
                Check(array[i], items, $"{path}[{i}]", problems);
            }
        }
    }

    private static bool Is(JsonNode? value, string type) => type switch
    {
        "null" => value is null,
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue v && v.TryGetValue<string>(out _),
        "boolean" => value is JsonValue v && v.TryGetValue<bool>(out _),
        "number" => value is JsonValue v && v.TryGetValue<double>(out _),
        "integer" => value is JsonValue v && v.TryGetValue<double>(out var d) && Math.Floor(d) == d,
        _ => true,
    };

    private static string Kind(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "an object",
        JsonArray => "an array",
        JsonValue v when v.TryGetValue<string>(out _) => "a string",
        JsonValue v when v.TryGetValue<bool>(out _) => "a boolean",
        _ => "a number",
    };

    private static bool Formatted(string text, string format) => format switch
    {
        "date-time" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "date" => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "uuid" => Guid.TryParse(text, out _),
        "uri" => Uri.TryCreate(text, UriKind.Absolute, out _),
        _ => true,
    };
}
