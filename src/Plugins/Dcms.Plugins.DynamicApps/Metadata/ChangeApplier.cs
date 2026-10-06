using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.DynamicApps.Metadata;

/// <summary>
/// Applies a change set to a configuration. Pure: no I/O, no clock, the same input gives the
/// same output, apart from the ids it assigns to new resources. Works on the JSON form so
/// create, update (a JSON merge patch) and delete are one implementation for every resource
/// kind; the typed model is rebuilt after each operation to reject a bad shape at the operation
/// that introduced it.
///
/// <para>Structure is checked here (the target exists, references resolve, ids are unique);
/// meaning is <see cref="ConfigValidator"/>'s. A draft may be invalid between change sets;
/// a publish may not.</para>
/// </summary>
public static class ChangeApplier
{
    public const int MaxOperations = 200;

    private static readonly string[] Ops = ["create", "update", "delete"];
    private static readonly string[] Types = ["settings", "table", "field", "index", "relationship", "choiceSet", "view"];

    /// <summary>
    /// The most a change set may carry, and the largest configuration document, in serialized
    /// characters. Every cost below is linear in these, so they bound the work a request can ask
    /// for. The document cap clears the validator's own limits (100 tables of 200 fields).
    /// </summary>
    public const int MaxChangeSetChars = 1_000_000;
    public const int MaxDocumentChars = 4_000_000;

    /// <exception cref="ContractValidationException">An operation is malformed or names something that does not exist.</exception>
    public static AppConfig Apply(AppConfig config, IReadOnlyList<ChangeOperation> operations)
    {
        if (operations.Count == 0)
        {
            throw new ContractValidationException("A change set needs at least one operation.");
        }
        if (operations.Count > MaxOperations)
        {
            throw new ContractValidationException($"A change set may hold at most {MaxOperations} operations; split it.");
        }
        if (operations.Sum(o => o.Value?.ToJsonString().Length ?? 0) > MaxChangeSetChars)
        {
            throw new ContractValidationException($"A change set may carry at most {MaxChangeSetChars:N0} characters of values; split it.");
        }

        var doc = ConfigJson.ToNode(config);
        var session = new Session(doc);
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            try
            {
                // Shape-checks just what this operation wrote, so a bad property is reported
                // against the operation that introduced it without re-reading the whole document
                // once per operation.
                if (session.Apply(op) is { } touched)
                {
                    ConfigJson.FromNode(touched.Node, touched.Shape);
                }
            }
            catch (ContractValidationException e)
            {
                var target = op.Target is null ? "" : $" {op.Target}";
                throw new ContractValidationException($"Operation {i + 1} ({op.Op} {op.Type}{target}): {e.Message}");
            }
        }
        if (doc.ToJsonString().Length > MaxDocumentChars)
        {
            throw new ContractValidationException(
                $"The configuration would exceed {MaxDocumentChars:N0} characters. Remove what is unused, or split the application.");
        }
        return ConfigJson.FromNode(doc);
    }

    private sealed class Session(JsonObject doc)
    {
        /// <summary>Every id in the document, collected once: checking each new id against a fresh walk was quadratic.</summary>
        private readonly HashSet<Guid> _ids = AllIds(doc).ToHashSet();

        private JsonArray Tables => Array(doc, "tables");
        private JsonArray Relationships => Array(doc, "relationships");
        private JsonArray ChoiceSets => Array(doc, "choiceSets");
        private JsonArray Views => Array(doc, "views");

        /// <summary>Applies one operation; returns what it created or patched, and the shape that must still hold.</summary>
        public (JsonObject Node, Type Shape)? Apply(ChangeOperation op)
        {
            if (!Ops.Contains(op.Op))
            {
                throw Invalid($"op must be one of {string.Join(", ", Ops)}.");
            }
            if (!Types.Contains(op.Type))
            {
                throw Invalid($"type must be one of {string.Join(", ", Types)}.");
            }
            switch (op.Op)
            {
                case "create":
                    return (Create(op.Type, op.Target, op.Value ?? throw Invalid("create needs a value.")), Shapes[op.Type]);
                case "update":
                    return (Update(op.Type, op.Target, op.Value ?? throw Invalid("update needs a value (a merge patch).")), Shapes[op.Type]);
                default:
                    Delete(op.Type, op.Target ?? throw Invalid("delete needs a target."));
                    return null;
            }
        }

        private static readonly Dictionary<string, Type> Shapes = new()
        {
            ["settings"] = typeof(AppSettings),
            ["table"] = typeof(TableDef),
            ["field"] = typeof(FieldDef),
            ["index"] = typeof(IndexDef),
            ["relationship"] = typeof(RelationshipDef),
            ["choiceSet"] = typeof(ChoiceSetDef),
            ["view"] = typeof(ViewDef),
        };

        // ---------------------------------------------------------------- create

        private JsonObject Create(string type, string? target, JsonObject source)
        {
            var value = source.DeepClone().AsObject();
            switch (type)
            {
                case "table":
                    AssignId(value);
                    foreach (var field in Array(value, "fields").Cast<JsonObject>())
                    {
                        AssignId(field);
                        ResolveChoiceSet(field);
                    }
                    foreach (var index in Array(value, "indexes").Cast<JsonObject>())
                    {
                        AssignId(index);
                        ResolveMembers(index, "fieldIds", value);
                    }
                    ResolveMember(value, "primaryFieldId", value);
                    Tables.Add(value);
                    break;
                case "field":
                {
                    var table = Table(target ?? throw Invalid("target must name the field's table."));
                    AssignId(value);
                    ResolveChoiceSet(value);
                    Array(table, "fields").Add(value);
                    break;
                }
                case "index":
                {
                    var table = Table(target ?? throw Invalid("target must name the index's table."));
                    AssignId(value);
                    ResolveMembers(value, "fieldIds", table);
                    Array(table, "indexes").Add(value);
                    break;
                }
                case "relationship":
                    AssignId(value);
                    ResolveTable(value, "sourceTableId");
                    ResolveTable(value, "targetTableId");
                    Relationships.Add(value);
                    break;
                case "choiceSet":
                    AssignId(value);
                    ChoiceSets.Add(value);
                    break;
                case "view":
                    AssignId(value);
                    ResolveTable(value, "tableId");
                    ResolveViewMembers(value, TableById(value["tableId"]));
                    Views.Add(value);
                    break;
                default:
                    throw Invalid("settings always exist; update them instead.");
            }
            return value;
        }

        // ---------------------------------------------------------------- update

        private JsonObject Update(string type, string? target, JsonObject source)
        {
            var patch = source.DeepClone().AsObject();
            if (patch.ContainsKey("id"))
            {
                throw Invalid("a resource's id never changes.");
            }

            JsonObject existing;
            switch (type)
            {
                case "settings":
                    existing = doc["settings"] as JsonObject ?? new JsonObject();
                    doc["settings"] = existing;
                    break;
                case "table":
                    if (patch.ContainsKey("fields") || patch.ContainsKey("indexes"))
                    {
                        throw Invalid("change a table's fields and indexes with field and index operations.");
                    }
                    existing = Table(Require(target));
                    ResolveMember(patch, "primaryFieldId", existing);
                    break;
                case "field":
                    existing = Field(Require(target)).Field;
                    ResolveChoiceSet(patch);
                    break;
                case "index":
                {
                    var (table, index) = Index(Require(target));
                    existing = index;
                    ResolveMembers(patch, "fieldIds", table);
                    break;
                }
                case "relationship":
                    existing = Relationship(Require(target));
                    ResolveTable(patch, "sourceTableId");
                    ResolveTable(patch, "targetTableId");
                    break;
                case "choiceSet":
                    existing = ChoiceSet(Require(target));
                    break;
                default:
                    existing = View(Require(target));
                    ResolveTable(patch, "tableId");
                    ResolveViewMembers(patch, TableById(patch["tableId"] ?? existing["tableId"]));
                    break;
            }
            MergePatch(existing, patch);
            return existing;
        }

        // ---------------------------------------------------------------- delete

        private void Delete(string type, string target)
        {
            switch (type)
            {
                case "table":
                {
                    var table = Table(target);
                    var id = IdOf(table);
                    Tables.Remove(table);
                    RemoveWhere(Views, v => SameId(v?["tableId"], id));
                    var touching = Relationships.Cast<JsonObject>()
                        .Where(r => SameId(r["sourceTableId"], id) || SameId(r["targetTableId"], id)).ToList();
                    foreach (var relationship in touching)
                    {
                        Relationships.Remove(relationship);
                        // A lookup from a surviving table to this one goes from that table's views too.
                        if (TableById(relationship["sourceTableId"]) is { } source)
                        {
                            ForgetMember(source, IdOf(relationship));
                        }
                    }
                    break;
                }
                case "field":
                {
                    var (table, field) = Field(target);
                    var id = IdOf(field);
                    Array(table, "fields").Remove(field);
                    ForgetMember(table, id);
                    break;
                }
                case "index":
                {
                    var (table, index) = Index(target);
                    Array(table, "indexes").Remove(index);
                    break;
                }
                case "relationship":
                {
                    var relationship = Relationship(target);
                    Relationships.Remove(relationship);
                    if (TableById(relationship["sourceTableId"]) is { } source)
                    {
                        ForgetMember(source, IdOf(relationship));
                    }
                    break;
                }
                case "choiceSet":
                    ChoiceSets.Remove(ChoiceSet(target));
                    break;
                case "view":
                    Views.Remove(View(target));
                    break;
                default:
                    throw Invalid("settings cannot be deleted.");
            }
        }

        /// <summary>A field or lookup is gone: drop it from the table's indexes and views and as its primary field.</summary>
        private void ForgetMember(JsonObject table, Guid id)
        {
            if (SameId(table["primaryFieldId"], id))
            {
                table.Remove("primaryFieldId");
            }
            foreach (var index in Array(table, "indexes").Cast<JsonObject>().ToList())
            {
                var fields = Array(index, "fieldIds");
                RemoveWhere(fields, f => SameId(f, id));
                if (fields.Count == 0)
                {
                    Array(table, "indexes").Remove(index);
                }
            }
            var tableId = IdOf(table);
            foreach (var view in Views.Cast<JsonObject>().Where(v => SameId(v["tableId"], tableId)))
            {
                RemoveWhere(Array(view, "columns"), c => SameId(c, id));
                RemoveWhere(Array(view, "sort"), s => SameId(s?["fieldId"], id));
            }
        }

        // ---------------------------------------------------------------- lookups

        private JsonObject Table(string reference) =>
            Find(Tables, reference) ?? throw Invalid($"no table '{reference}'.");

        private JsonObject? TableById(JsonNode? id) =>
            AsGuid(id) is { } guid ? Tables.Cast<JsonObject>().FirstOrDefault(t => IdOf(t) == guid) : null;

        private (JsonObject Table, JsonObject Field) Field(string reference) =>
            Member(reference, "fields") ?? throw Invalid($"no field '{reference}'.");

        private (JsonObject Table, JsonObject Index) Index(string reference) =>
            Member(reference, "indexes") ?? throw Invalid($"no index '{reference}'.");

        private JsonObject Relationship(string reference)
        {
            if (Guid.TryParse(reference, out var id))
            {
                return Relationships.Cast<JsonObject>().FirstOrDefault(r => IdOf(r) == id)
                       ?? throw Invalid($"no relationship '{reference}'.");
            }
            var (table, name) = Split(reference);
            var tableId = IdOf(Table(table));
            return Relationships.Cast<JsonObject>().FirstOrDefault(r =>
                       SameId(r["sourceTableId"], tableId) && Name(r) == name)
                   ?? throw Invalid($"no relationship '{reference}' (address it as sourceTable.apiName).");
        }

        private JsonObject ChoiceSet(string reference) =>
            Find(ChoiceSets, reference) ?? throw Invalid($"no choice set '{reference}'.");

        private JsonObject View(string reference)
        {
            if (Guid.TryParse(reference, out var id))
            {
                return Views.Cast<JsonObject>().FirstOrDefault(v => IdOf(v) == id)
                       ?? throw Invalid($"no view '{reference}'.");
            }
            var (table, name) = Split(reference);
            var tableId = IdOf(Table(table));
            return Views.Cast<JsonObject>().FirstOrDefault(v => SameId(v["tableId"], tableId) && Name(v) == name)
                   ?? throw Invalid($"no view '{reference}' (address it as table.apiName).");
        }

        /// <summary>A field or index, by id anywhere or by <c>table.apiName</c>.</summary>
        private (JsonObject, JsonObject)? Member(string reference, string collection)
        {
            if (Guid.TryParse(reference, out var id))
            {
                foreach (var table in Tables.Cast<JsonObject>())
                {
                    if (Array(table, collection).Cast<JsonObject>().FirstOrDefault(m => IdOf(m) == id) is { } hit)
                    {
                        return (table, hit);
                    }
                }
                return null;
            }
            var (tableName, name) = Split(reference);
            var owner = Table(tableName);
            return Array(owner, collection).Cast<JsonObject>().FirstOrDefault(m => Name(m) == name) is { } member
                ? (owner, member)
                : null;
        }

        // ---------------------------------------------------------------- reference resolution

        private void ResolveTable(JsonObject value, string key)
        {
            if (value[key] is JsonValue v && v.TryGetValue<string>(out var reference) && !Guid.TryParse(reference, out _))
            {
                value[key] = IdOf(Table(reference)).ToString();
            }
        }

        private void ResolveChoiceSet(JsonObject value)
        {
            if (value["choiceSetId"] is JsonValue v && v.TryGetValue<string>(out var reference) && !Guid.TryParse(reference, out _))
            {
                value["choiceSetId"] = IdOf(ChoiceSet(reference)).ToString();
            }
        }

        private void ResolveMember(JsonObject value, string key, JsonObject table)
        {
            if (value[key] is JsonValue v && v.TryGetValue<string>(out var reference))
            {
                value[key] = MemberId(reference, table);
            }
        }

        private void ResolveMembers(JsonObject value, string key, JsonObject table)
        {
            if (value[key] is not JsonArray list)
            {
                return;
            }
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is JsonValue v && v.TryGetValue<string>(out var reference))
                {
                    list[i] = MemberId(reference, table);
                }
            }
        }

        private void ResolveViewMembers(JsonObject view, JsonObject? table)
        {
            if (table is null)
            {
                return; // the validator reports the missing table
            }
            ResolveMembers(view, "columns", table);
            if (view["sort"] is JsonArray sort)
            {
                foreach (var entry in sort.OfType<JsonObject>())
                {
                    ResolveMember(entry, "fieldId", table);
                }
            }
        }

        /// <summary>A field of the table, or a lookup relationship whose source it is, by api name; an id passes through.</summary>
        private string MemberId(string reference, JsonObject table)
        {
            if (Guid.TryParse(reference, out var id))
            {
                return id.ToString();
            }
            if (Array(table, "fields").Cast<JsonObject>().FirstOrDefault(f => Name(f) == reference) is { } field)
            {
                return IdOf(field).ToString();
            }
            var tableId = IdOf(table);
            if (Relationships.Cast<JsonObject>().FirstOrDefault(r =>
                    SameId(r["sourceTableId"], tableId) && Name(r) == reference && r["kind"]?.GetValue<string>() != "manyToMany") is { } lookup)
            {
                return IdOf(lookup).ToString();
            }
            throw Invalid($"table '{Name(table)}' has no field '{reference}'.");
        }

        // ---------------------------------------------------------------- ids

        private void AssignId(JsonObject value)
        {
            if (value["id"] is null)
            {
                var fresh = Guid.NewGuid();
                _ids.Add(fresh);
                value["id"] = fresh.ToString();
                return;
            }
            var id = AsGuid(value["id"]) ?? throw Invalid("id must be a uuid.");
            if (!_ids.Add(id))
            {
                throw Invalid($"id {id} is already in use.");
            }
            value["id"] = id.ToString();
        }

        private static IEnumerable<Guid> AllIds(JsonNode? node) => node switch
        {
            JsonObject obj => (AsGuid(obj["id"]) is { } id ? [id] : Enumerable.Empty<Guid>())
                .Concat(obj.Where(p => p.Key != "id").SelectMany(p => AllIds(p.Value))),
            JsonArray arr => arr.SelectMany(AllIds),
            _ => [],
        };
    }

    // -------------------------------------------------------------------- helpers

    internal static void MergePatch(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (value is null)
            {
                target.Remove(key);
            }
            else if (value is JsonObject inner && target[key] is JsonObject existing)
            {
                MergePatch(existing, inner);
            }
            else
            {
                target[key] = value.DeepClone();
            }
        }
    }

    private static JsonArray Array(JsonObject owner, string key)
    {
        if (owner[key] is JsonArray array)
        {
            return array;
        }
        if (owner[key] is not null)
        {
            throw Invalid($"{key} must be a list.");
        }
        var created = new JsonArray();
        owner[key] = created;
        return created;
    }

    private static JsonObject? Find(JsonArray items, string reference) =>
        Guid.TryParse(reference, out var id)
            ? items.Cast<JsonObject>().FirstOrDefault(i => IdOf(i) == id)
            : items.Cast<JsonObject>().FirstOrDefault(i => Name(i) == reference);

    private static void RemoveWhere(JsonArray items, Func<JsonNode?, bool> predicate)
    {
        foreach (var item in items.Where(predicate).ToList())
        {
            items.Remove(item);
        }
    }

    private static (string Table, string Name) Split(string reference)
    {
        var dot = reference.IndexOf('.');
        return dot > 0 && dot < reference.Length - 1
            ? (reference[..dot], reference[(dot + 1)..])
            : throw Invalid($"'{reference}' must be an id or table.apiName.");
    }

    private static string Require(string? target) => target ?? throw Invalid("update needs a target.");

    private static string? Name(JsonObject item) =>
        item["apiName"] is JsonValue v && v.TryGetValue<string>(out var name) ? name : null;

    private static Guid IdOf(JsonObject item) => AsGuid(item["id"]) ?? Guid.Empty;

    private static Guid? AsGuid(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) ? g : null;

    private static bool SameId(JsonNode? node, Guid id) => AsGuid(node) == id;

    private static ContractValidationException Invalid(string message) => new(message);
}
