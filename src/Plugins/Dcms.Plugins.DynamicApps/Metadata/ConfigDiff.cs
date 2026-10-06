using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;

namespace Dcms.Plugins.DynamicApps.Metadata;

/// <summary>
/// The logical differences between two configurations, matched by id: what the change log
/// records for a change set, what a preview shows against the live revision, and what a
/// revision diff lists. Deterministic: sorted by resource kind, then path, then operation.
/// </summary>
public static class ConfigDiff
{
    private static readonly string[] KindOrder = ["settings", "choiceSet", "table", "field", "relationship", "index", "view"];
    private static readonly string[] OpOrder = ["create", "update", "delete"];

    public static IReadOnlyList<ConfigChange> Between(AppConfig before, AppConfig after)
    {
        var a = Flatten(before);
        var b = Flatten(after);
        var changes = new List<ConfigChange>();

        foreach (var (key, old) in a)
        {
            if (!b.TryGetValue(key, out var now))
            {
                changes.Add(new ConfigChange
                {
                    Op = "delete", ResourceType = key.Kind, ResourceId = key.Id, Path = old.Path,
                    Before = old.Node, Destructive = true,
                });
            }
            else if (!JsonNode.DeepEquals(Comparable(key, old.Node, now.Node), Comparable(key, now.Node, old.Node)))
            {
                changes.Add(new ConfigChange
                {
                    Op = "update", ResourceType = key.Kind, ResourceId = key.Id, Path = now.Path,
                    Before = old.Node, After = now.Node, Destructive = Tightens(key.Kind, old.Node, now.Node),
                });
            }
        }
        // A new required field or lookup on a table that already existed has no value in the
        // rows that table already holds.
        var existingTables = before.Tables.Select(t => t.Id).ToHashSet();
        var tableOfField = after.Tables.SelectMany(t => t.Fields.Select(f => (f.Id, Table: t.Id))).DistinctBy(x => x.Id).ToDictionary(x => x.Id, x => x.Table);
        var sourceOf = after.Relationships.DistinctBy(r => r.Id).ToDictionary(r => r.Id, r => r.SourceTableId);
        foreach (var (key, now) in b)
        {
            if (!a.ContainsKey(key))
            {
                var owner = key.Kind switch
                {
                    "field" => tableOfField.GetValueOrDefault(key.Id!.Value),
                    "relationship" => sourceOf.GetValueOrDefault(key.Id!.Value),
                    _ => Guid.Empty,
                };
                changes.Add(new ConfigChange
                {
                    Op = "create", ResourceType = key.Kind, ResourceId = key.Id, Path = now.Path, After = now.Node,
                    Destructive = Flag(now.Node, "required") && existingTables.Contains(owner),
                });
            }
        }

        return changes
            .OrderBy(c => System.Array.IndexOf(KindOrder, c.ResourceType))
            .ThenBy(c => c.Path, StringComparer.Ordinal)
            .ThenBy(c => System.Array.IndexOf(OpOrder, c.Op))
            .ToList();
    }

    /// <summary>
    /// A table compared on the order of the fields both sides have: adding or removing a field
    /// is that field's change, not its table's; only moving one is the table's.
    /// </summary>
    private static JsonNode Comparable(Key key, JsonNode node, JsonNode other)
    {
        if (key.Kind != "table")
        {
            return node;
        }
        var shared = (other["fieldOrder"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToHashSet();
        var copy = node.DeepClone().AsObject();
        copy["fieldOrder"] = new JsonArray((node["fieldOrder"] as JsonArray ?? [])
            .Select(n => n!.GetValue<string>()).Where(shared.Contains).Select(id => (JsonNode)id).ToArray());
        return copy;
    }

    /// <summary>An update that existing data may not survive: a type change, a new constraint, a removed choice.</summary>
    private static bool Tightens(string kind, JsonNode? before, JsonNode? after) => kind switch
    {
        "field" => Text(before, "type") != Text(after, "type")
                   || (!Flag(before, "required") && Flag(after, "required"))
                   || (!Flag(before, "unique") && Flag(after, "unique")),
        "relationship" => Text(before, "kind") != Text(after, "kind")
                          || Text(before, "targetTableId") != Text(after, "targetTableId")
                          || (!Flag(before, "required") && Flag(after, "required")),
        "index" => !Flag(before, "unique") && Flag(after, "unique"),
        "choiceSet" => Values(before).Except(Values(after)).Any(),
        _ => false,
    };

    private readonly record struct Key(string Kind, Guid? Id);

    private readonly record struct Entry(string Path, JsonNode Node);

    private static Dictionary<Key, Entry> Flatten(AppConfig config)
    {
        var tables = config.Tables.DistinctBy(t => t.Id).ToDictionary(t => t.Id, t => t.ApiName);
        string TableName(Guid id) => tables.GetValueOrDefault(id, id.ToString());

        var items = new Dictionary<Key, Entry>
        {
            [new Key("settings", null)] = new("settings", Node(config.Settings)),
        };
        foreach (var set in config.ChoiceSets)
        {
            Add(items, "choiceSet", set.Id, set.ApiName, Node(set));
        }
        foreach (var table in config.Tables)
        {
            // The table itself without its children, which are diffed on their own, plus their
            // order: moving a field is a change to the table, not to the field.
            var node = Node(table with { Fields = [], Indexes = [] });
            node.Remove("fields");
            node.Remove("indexes");
            node["fieldOrder"] = new JsonArray(table.Fields.Select(f => (JsonNode)f.Id.ToString()).ToArray());
            Add(items, "table", table.Id, table.ApiName, node);

            foreach (var field in table.Fields)
            {
                Add(items, "field", field.Id, $"{table.ApiName}.{field.ApiName}", Node(field));
            }
            foreach (var index in table.Indexes)
            {
                Add(items, "index", index.Id, $"{table.ApiName}.{index.ApiName}", Node(index));
            }
        }
        foreach (var relationship in config.Relationships)
        {
            Add(items, "relationship", relationship.Id, $"{TableName(relationship.SourceTableId)}.{relationship.ApiName}", Node(relationship));
        }
        foreach (var view in config.Views)
        {
            Add(items, "view", view.Id, $"{TableName(view.TableId)}.{view.ApiName}", Node(view));
        }
        return items;
    }

    private static void Add(Dictionary<Key, Entry> items, string kind, Guid id, string path, JsonObject node) =>
        // A duplicated id is the validator's to report; the diff keeps the first.
        items.TryAdd(new Key(kind, id), new Entry(path, node));

    private static JsonObject Node<T>(T value) =>
        ConfigJson.Sorted(System.Text.Json.JsonSerializer.SerializeToNode(value, ConfigJson.Options))!.AsObject();

    private static bool Flag(JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string? Text(JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static IEnumerable<string> Values(JsonNode? choiceSet) =>
        (choiceSet?["options"] as JsonArray ?? []).Select(o => Text(o, "value") ?? "");
}
