using System.Text.Json.Nodes;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// One scope of what duplicating an environment can copy beyond its configuration (ADR 0028):
/// Dynamic Apps records, plugin data, form submissions. The host and plugins contribute these;
/// whoever duplicates ticks the scopes they want.
///
/// <para>Runs after the configuration is in the new environment, so the plugin instances it
/// names exist there. Rows get new ids; every id given out is recorded in
/// <see cref="EnvironmentDataCopy.Ids"/> so that references to it -- in this copier's rows and in
/// every later one's -- can be rewritten (<see cref="EnvironmentDataCopy.Remap(string)"/>).</para>
/// </summary>
public interface IEnvironmentDataCopier
{
    /// <summary>Stable id, e.g. <c>dynamic-apps-records</c>; also the i18n key's last segment.</summary>
    string Scope { get; }

    /// <summary>Lowest first; a scope whose rows others refer to goes before them.</summary>
    int Order => 100;

    /// <summary>The rows are about people (submissions, visitor accounts, chats): offered with a warning.</summary>
    bool PersonalData => false;

    /// <returns>How many rows were copied.</returns>
    Task<int> CopyAsync(EnvironmentDataCopy copy, CancellationToken ct);
}

/// <summary>One duplication in progress, shared by every copier in it.</summary>
public sealed class EnvironmentDataCopy
{
    public required Guid TenantId { get; init; }
    public required Guid SourceEnvironmentId { get; init; }
    public required Guid TargetEnvironmentId { get; init; }

    /// <summary>Plugin instances: the source's id to the target's, matched by slug.</summary>
    public required IReadOnlyDictionary<Guid, Guid> Instances { get; init; }

    /// <summary>Opens the source environment's database scope: read inside it.</summary>
    public required Func<IDisposable> InSource { get; init; }

    /// <summary>Opens the target environment's database scope: write inside it.</summary>
    public required Func<IDisposable> InTarget { get; init; }

    /// <summary>Every id given a new one so far: source id to target id.</summary>
    public Dictionary<Guid, Guid> Ids { get; } = [];

    /// <summary>A new id for <paramref name="source"/>, recorded; the same one when asked again.</summary>
    public Guid NewId(Guid source)
    {
        if (!Ids.TryGetValue(source, out var target)) Ids[source] = target = Guid.NewGuid();
        return target;
    }

    /// <summary>The id as the target knows it: remapped, a plugin instance's counterpart, or unchanged.</summary>
    public Guid Map(Guid source) =>
        Ids.TryGetValue(source, out var id) ? id : Instances.TryGetValue(source, out var instance) ? instance : source;

    /// <summary>JSON with every string that is a remapped id replaced by its new one.</summary>
    public string Remap(string json) => Ids.Count == 0 && Instances.Count == 0
        ? json
        : Remap(JsonNode.Parse(json))?.ToJsonString() ?? json;

    private JsonNode? Remap(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Remap(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Remap).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var id) && Map(id) is var mapped && mapped != id
            => JsonValue.Create(mapped.ToString()),
        _ => node?.DeepClone(),
    };
}
