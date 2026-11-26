using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dcms.Plugins.WebSockets.Api;
using Json.Schema;

namespace Dcms.Plugins.WebSockets;

/// <summary>
/// An instance's channels, read from its config, and the payload checks every send and publish
/// goes through. The config schema (<see cref="WebSocketsPlugin"/>) already restricts each
/// channel's payload schema to the supported subset; this class only reads and applies it.
/// </summary>
public static partial class Channels
{
    /// <summary>A channel name: what config allows, also checked on every hub call.</summary>
    public static Regex NamePattern() => ChannelConfig.NamePattern();

    /// <summary>A channel key: URL-safe and short, so group names stay bounded.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$")]
    public static partial Regex KeyPattern();

    /// <summary>Largest payload a server send may carry (browser frames are capped by the hub at 16 KB).</summary>
    public const int MaxPayloadBytes = 32 * 1024;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <inheritdoc cref="ChannelConfig.Read"/>
    public static IReadOnlyList<ChannelDefinition> Read(JsonElement config) => ChannelConfig.Read(config);

    public static ChannelDefinition? Find(JsonElement config, string name) =>
        Read(config).FirstOrDefault(c => c.Name == name);

    /// <summary>The config with <paramref name="channels"/> in place of its channel list; other keys are kept.</summary>
    public static JsonElement With(JsonElement config, IEnumerable<ChannelDefinition> channels)
    {
        var root = config.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(config.GetRawText())!.AsObject()
            : new JsonObject();
        var array = new JsonArray();
        foreach (var channel in channels)
        {
            var node = JsonSerializer.SerializeToNode(channel, Web)!.AsObject();
            // Keep the stored config as small as the console writes it: no nulls.
            foreach (var key in node.Where(p => p.Value is null).Select(p => p.Key).ToList())
            {
                node.Remove(key);
            }
            array.Add(node);
        }
        root["channels"] = array;
        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>
    /// A stable hash of what the generated clients are built from (names, access, schemas), so the
    /// OpenAPI cache key and the IDE's API fingerprint change exactly when a client would.
    /// </summary>
    public static string Version(JsonElement config)
    {
        var text = JsonSerializer.Serialize(Read(config), Web);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    /// <summary>Null when <paramref name="key"/> fits <paramref name="channel"/>; otherwise why not.</summary>
    public static string? CheckKey(ChannelDefinition channel, string? key) => (channel.Keyed, key) switch
    {
        (true, null or "") => $"Channel '{channel.Name}' is keyed: give a key.",
        (true, _) when !KeyPattern().IsMatch(key!) => "A key is 1-128 letters, digits and _ . : - characters.",
        (false, { Length: > 0 }) => $"Channel '{channel.Name}' is not keyed: leave the key out.",
        _ => null,
    };

    private static readonly BuildOptions Build = new()
    {
        Dialect = Dialect.Default.With([], allowUnknownKeywords: true),
    };

    // ponytail: unbounded by distinct schema text, which only an edit creates; an LRU if tenants
    // ever churn thousands of schemas per process.
    private static readonly ConcurrentDictionary<string, JsonSchema> Compiled = new(StringComparer.Ordinal);

    /// <summary>Null when the payload is acceptable for the channel; otherwise the problems.</summary>
    public static IReadOnlyList<string>? CheckPayload(ChannelDefinition channel, JsonElement payload, int maxBytes = MaxPayloadBytes)
    {
        var raw = payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(raw) > maxBytes)
        {
            return [$"The payload is larger than {maxBytes / 1024} KB."];
        }
        if (channel.Schema is not { ValueKind: JsonValueKind.Object } schemaElement)
        {
            return null;
        }
        var text = schemaElement.GetRawText();
        var schema = Compiled.GetOrAdd(text, t => JsonSchema.FromText(t, Build));
        var result = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
        {
            return null;
        }
        return Flatten(result)
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
            .DefaultIfEmpty("The payload does not match the channel's schema.")
            .Take(10)
            .ToList();
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;
        foreach (var child in (results.Details ?? []).SelectMany(Flatten))
        {
            yield return child;
        }
    }
}
