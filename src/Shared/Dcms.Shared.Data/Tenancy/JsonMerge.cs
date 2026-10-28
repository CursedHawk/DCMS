using System.Text.Json.Nodes;

namespace Dcms.Shared.Data.Tenancy;

/// <summary>One place a three-way merge could not decide: both sides changed it, differently.</summary>
/// <param name="Path">Where, as <c>/tables/deals/fields/amount/type</c>: properties by name, keyed array items by key.</param>
public sealed record MergeConflict(string Path, JsonNode? Ours, JsonNode? Theirs);

/// <summary>
/// A field-level three-way merge of configuration values (ADR 0026): what <c>theirs</c> changed
/// since <c>base</c> is laid onto <c>ours</c>, and only that, so a migration's change to one field
/// never carries along another edit made to the same item.
/// <list type="bullet">
/// <item>Objects merge property by property.</item>
/// <item>Arrays of objects that all carry a unique <c>id</c>, <c>apiName</c>, <c>key</c> or
/// <c>value</c> merge item by item under that key.</item>
/// <item>Arrays of scalars merge as sets, as long as neither side reordered what the other kept.</item>
/// <item>Anything else takes the side that changed; both changed differently is a conflict.</item>
/// </list>
/// A conflict is settled by <c>choose</c> (<c>"ours"</c> or <c>"theirs"</c> for its path); one it
/// leaves open is reported, and the merged value keeps ours there.
/// </summary>
public static class JsonMerge
{
    private static readonly string[] KeyFields = ["id", "apiName", "key", "value"];

    public static (JsonNode? Merged, List<MergeConflict> Conflicts) ThreeWay(
        JsonNode? @base, JsonNode? ours, JsonNode? theirs, Func<string, string?>? choose = null)
    {
        var conflicts = new List<MergeConflict>();
        var merged = Merge("", @base, ours, theirs, choose ?? (_ => null), conflicts);
        return (merged, conflicts);
    }

    private static JsonNode? Merge(string path, JsonNode? b, JsonNode? o, JsonNode? t, Func<string, string?> choose, List<MergeConflict> conflicts)
    {
        if (Eq(o, t)) return Copy(o);
        if (Eq(b, o)) return Copy(t);
        if (Eq(b, t)) return Copy(o);

        if (o is JsonObject oo && t is JsonObject to && (b is null or JsonObject))
        {
            var bo = b as JsonObject;
            var result = new JsonObject();
            foreach (var name in oo.Select(p => p.Key).Concat(to.Select(p => p.Key)).Distinct())
            {
                var value = Merge($"{path}/{Escape(name)}", bo?[name], oo[name], to[name], choose, conflicts);
                if (value is not null || (oo.ContainsKey(name) && to.ContainsKey(name))) result[name] = value;
            }
            return result;
        }
        if (o is JsonArray oa && t is JsonArray ta && (b is null or JsonArray))
        {
            var ba = b as JsonArray ?? [];
            if (KeyOf(ba, oa, ta) is { } key) return MergeKeyed(path, key, ba, oa, ta, choose, conflicts);
            if (AllScalars(ba, oa, ta) && SameOrder(ba, oa, ta)) return MergeSet(ba, oa, ta);
        }
        return Decide(path, o, t, choose, conflicts);
    }

    private static JsonNode? Decide(string path, JsonNode? o, JsonNode? t, Func<string, string?> choose, List<MergeConflict> conflicts)
    {
        switch (choose(path))
        {
            case "theirs": return Copy(t);
            case "ours": return Copy(o);
            default:
                conflicts.Add(new MergeConflict(path, Copy(o), Copy(t)));
                return Copy(o);
        }
    }

    private static JsonArray MergeKeyed(string path, string key, JsonArray b, JsonArray o, JsonArray t,
        Func<string, string?> choose, List<MergeConflict> conflicts)
    {
        static Dictionary<string, JsonNode?> ByKey(JsonArray a, string key) =>
            a.ToDictionary(n => KeyValue(n, key)!, n => n, StringComparer.Ordinal);
        var bk = ByKey(b, key);
        var ok = ByKey(o, key);
        var tk = ByKey(t, key);
        var result = new JsonArray();
        // Ours' order first, then what theirs added, in theirs' order.
        foreach (var k in ok.Keys.Concat(tk.Keys.Where(k => !ok.ContainsKey(k))))
        {
            var value = Merge($"{path}/{Escape(k)}", bk.GetValueOrDefault(k), ok.GetValueOrDefault(k), tk.GetValueOrDefault(k), choose, conflicts);
            if (value is not null) result.Add(value);
        }
        return result;
    }

    private static JsonArray MergeSet(JsonArray b, JsonArray o, JsonArray t)
    {
        var baseSet = b.Select(Text).ToHashSet(StringComparer.Ordinal);
        var theirsSet = t.Select(Text).ToHashSet(StringComparer.Ordinal);
        var removed = baseSet.Where(x => !theirsSet.Contains(x)).ToHashSet(StringComparer.Ordinal);
        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in o.Concat(t.Where(x => !baseSet.Contains(Text(x)))))
        {
            var text = Text(item);
            if (!removed.Contains(text) && seen.Add(text)) result.Add(Copy(item));
        }
        return result;
    }

    /// <summary>The key field every item of all three arrays has, uniquely within each; null when none does.</summary>
    private static string? KeyOf(params JsonArray[] arrays)
    {
        var items = arrays.SelectMany(a => a).ToList();
        if (items.Count == 0 || items.Any(i => i is not JsonObject)) return null;
        return KeyFields.FirstOrDefault(field =>
            items.All(i => KeyValue(i, field) is not null)
            && arrays.All(a => a.Select(i => KeyValue(i, field)).Distinct(StringComparer.Ordinal).Count() == a.Count));
    }

    private static string? KeyValue(JsonNode? item, string field) =>
        item is JsonObject obj && obj[field] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null;

    private static bool AllScalars(params JsonArray[] arrays) =>
        arrays.SelectMany(a => a).All(i => i is JsonValue);

    /// <summary>Whether the items all three share appear in the same order in each: a set merge would otherwise lose a reorder.</summary>
    private static bool SameOrder(JsonArray b, JsonArray o, JsonArray t)
    {
        var common = b.Select(Text).Where(x => o.Any(i => Text(i) == x) && t.Any(i => Text(i) == x)).ToHashSet(StringComparer.Ordinal);
        List<string> Order(JsonArray a) => a.Select(Text).Where(common.Contains).Distinct().ToList();
        var bo = Order(b);
        return bo.SequenceEqual(Order(o)) && bo.SequenceEqual(Order(t));
    }

    private static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static string Escape(string segment) => segment.Replace("~", "~0").Replace("/", "~1");

    private static bool Eq(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    private static JsonNode? Copy(JsonNode? node) => node?.DeepClone();
}
