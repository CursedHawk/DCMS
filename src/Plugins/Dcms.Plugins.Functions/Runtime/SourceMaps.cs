using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dcms.Plugins.Functions.Runtime;

/// <summary>
/// A TypeScript function's stack frames, mapped from its bundle back to the repository (workbench
/// W3): the harness reports <c>function:12:5</c>, a line and column of the esbuild bundle, and
/// the build's source map says which file and line of the repository that was. Only what the
/// map knows is rewritten; anything else stays as it was.
/// </summary>
public static partial class SourceMaps
{
    [GeneratedRegex(@"\bfunction:(\d+):(\d+)")]
    private static partial Regex Frame();

    public static string Map(string text, string? sourceMap)
    {
        if (string.IsNullOrEmpty(sourceMap) || !text.Contains("function:", StringComparison.Ordinal)) return text;
        Decoded? map;
        try
        {
            map = Decode(sourceMap);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return text;
        }
        return Frame().Replace(text, m =>
            map.Find(int.Parse(m.Groups[1].Value) - 1, int.Parse(m.Groups[2].Value) - 1) is { } at
                ? $"{at.Source}:{at.Line + 1}:{at.Column + 1}"
                : m.Value);
    }

    private sealed record Segment(int Column, int Source, int Line, int SourceColumn);

    private sealed class Decoded(string[] sources, List<List<Segment>> lines)
    {
        public (string Source, int Line, int Column)? Find(int line, int column)
        {
            if (line < 0 || line >= lines.Count) return null;
            var best = lines[line].LastOrDefault(s => s.Column <= column) ?? lines[line].FirstOrDefault();
            return best is null || best.Source >= sources.Length ? null : (sources[best.Source], best.Line, best.SourceColumn);
        }
    }

    private static Decoded Decode(string json)
    {
        using var document = JsonDocument.Parse(json);
        // esbuild writes sources relative to the bundle, which sits in the build's .out/.
        // The SDK's runtime is bundled in from .dcms/: its frames are the SDK's, not a file of the repository.
        var sources = document.RootElement.GetProperty("sources").EnumerateArray()
            .Select(s => Relative((s.GetString() ?? "").Replace('\\', '/')))
            .Select(s => s.StartsWith(".dcms/", StringComparison.Ordinal) ? "@dcms/functions" : s)
            .ToArray();
        var mappings = document.RootElement.GetProperty("mappings").GetString() ?? "";
        var lines = new List<List<Segment>>();
        int source = 0, line = 0, sourceColumn = 0;
        foreach (var generated in mappings.Split(';'))
        {
            var segments = new List<Segment>();
            var column = 0;
            foreach (var part in generated.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var values = Vlq(part);
                column += values[0];
                if (values.Count < 4) continue;
                source += values[1];
                line += values[2];
                sourceColumn += values[3];
                segments.Add(new Segment(column, source, line, sourceColumn));
            }
            lines.Add(segments);
        }
        return new Decoded(sources, lines);
    }

    private static string Relative(string path)
    {
        while (path.StartsWith("../", StringComparison.Ordinal)) path = path[3..];
        return path;
    }

    private const string Base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>Base64 VLQ, as source maps encode each field: 5 bits a digit, sign in the lowest bit.</summary>
    private static List<int> Vlq(string text)
    {
        var values = new List<int>();
        int value = 0, shift = 0;
        foreach (var c in text)
        {
            var digit = Base64.IndexOf(c);
            if (digit < 0) throw new FormatException($"'{c}' is not base64.");
            value += (digit & 31) << shift;
            if ((digit & 32) != 0)
            {
                shift += 5;
                continue;
            }
            values.Add((value & 1) == 1 ? -(value >> 1) : value >> 1);
            value = shift = 0;
        }
        return values;
    }
}
