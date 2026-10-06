using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.PluginSdk.Abstractions;

namespace Dcms.Plugins.DynamicApps.Data;

/// <summary>
/// What the public site sees of an application, from its published model only: the tables it
/// may read or write, their visible fields, and the OpenAPI that describes them. Descriptive —
/// the routes enforce access themselves; nothing here is a security boundary (ADR 0021).
/// </summary>
public static class PublicApi
{
    public static IEnumerable<RuntimeTable> Tables(RuntimeModel model) =>
        model.ByName.Values.Where(t => t.Def.Public.Read != PublicRead.None || t.Def.Public.Create).OrderBy(t => t.ApiName, StringComparer.Ordinal);

    private static IEnumerable<RuntimeMember> Visible(RuntimeTable table) =>
        table.Members.Where(m => RecordCodec.Visible(m, RecordPlane.Public));

    private static bool Writable(RuntimeMember member) => member.Field is not { ReadOnly: true } and not { Deprecated: true };

    // ------------------------------------------------------------------ model description

    /// <summary>The published model as the site may know it: <c>GET /api/{slug}/_model</c>.</summary>
    public static JsonObject Model(RuntimeModel model) => new()
    {
        ["revision"] = model.Revision.Number,
        ["hash"] = model.Revision.Hash,
        ["tables"] = new JsonArray(Tables(model).Select(t => (JsonNode)new JsonObject
        {
            ["apiName"] = t.ApiName,
            ["displayName"] = t.Def.DisplayName,
            ["pluralName"] = t.Def.PluralName,
            ["description"] = t.Def.Description,
            ["primaryField"] = t.Primary is { } p && RecordCodec.Visible(p, RecordPlane.Public) ? p.ApiName : null,
            ["access"] = new JsonObject
            {
                ["read"] = Camel(t.Def.Public.Read.ToString()),
                ["create"] = t.Def.Public.Create,
                ["updateOwn"] = t.Def.Public.UpdateOwn,
                ["deleteOwn"] = t.Def.Public.DeleteOwn,
            },
            ["fields"] = new JsonArray(Visible(t).Select(m => (JsonNode)new JsonObject
            {
                ["apiName"] = m.ApiName,
                ["displayName"] = m.Field?.DisplayName ?? m.Lookup!.DisplayName ?? m.ApiName,
                ["type"] = m.IsLookup ? "lookup" : Camel(m.Field!.Type.ToString()),
                ["required"] = m.Required,
                ["readOnly"] = !Writable(m),
                ["target"] = m.IsLookup ? model.ById.GetValueOrDefault(m.Lookup!.TargetTableId)?.ApiName : null,
                ["options"] = m.Options is { } options ? new JsonArray(options.Select(o => (JsonNode)o).ToArray()) : null,
            }).ToArray()),
            ["relationships"] = new JsonArray(t.Navigations.Values
                .Where(n => model.ById.TryGetValue(n.OtherTableId, out var other) && other.Def.Public.Read != PublicRead.None)
                .Select(n => (JsonNode)new JsonObject
                {
                    ["apiName"] = n.ApiName,
                    ["kind"] = Camel(n.Kind.ToString()),
                    ["target"] = model.ById[n.OtherTableId].ApiName,
                }).ToArray()),
        }).ToArray()),
    };

    // ------------------------------------------------------------------ OpenAPI

    /// <summary>The OpenAPI fragment for <c>/api/{slug}/…</c>, tagged with the published revision it describes.</summary>
    public static OpenApiFragment Fragment(PluginInstanceContext instance, RuntimeModel model, PluginManifest manifest)
    {
        var slug = instance.Slug;
        var paths = new List<OpenApiPathFragment>();
        var schemas = new Dictionary<string, JsonNode>();
        var revision = new Dictionary<string, JsonNode> { ["x-dcms-model-revision"] = model.Revision.Number };
        string Describe(string what) => $"{instance.Description}\n\n{what}\n\nPlugin: {manifest.Name} v{manifest.Version}, model revision {model.Revision.Number}";

        foreach (var table in Tables(model))
        {
            var name = table.ApiName;
            var record = $"{slug}_{name}";
            var input = $"{slug}_{name}_input";
            var page = $"{slug}_{name}_page";
            schemas[record] = RecordSchema(table, model);
            schemas[input] = InputSchema(table, forCreate: true);
            schemas[$"{input}_patch"] = InputSchema(table, forCreate: false);
            schemas[page] = PageSchema(record);
            var access = table.Def.Public;
            var label = table.Def.PluralName ?? table.Def.DisplayName;

            if (access.Read != PublicRead.None)
            {
                var own = access.Read == PublicRead.Own ? " Only the signed-in visitor's own records." : "";
                paths.Add(new OpenApiPathFragment($"/data/{name}", "get", $"{slug}_{name}_list", $"List {label}",
                    Describe($"A page of {label}.{own} `sort` is a field name, `-` first for descending; `select` and `expand` are comma-separated."),
                    Ref(page), Parameters: ListParameters(), ClientPath: [name, "list"], Extensions: revision));
                paths.Add(new OpenApiPathFragment($"/data/{name}/query", "post", $"{slug}_{name}_query", $"Query {label}",
                    Describe($"Filter, sort and page {label} with a query object.{own}"),
                    Ref(page), RequestBodySchema: QuerySchema(), ClientPath: [name, "query"], Extensions: revision));
                paths.Add(new OpenApiPathFragment($"/data/{name}/{{id}}", "get", $"{slug}_{name}_get", $"Get one {table.Def.DisplayName}",
                    Describe($"One {table.Def.DisplayName} by id.{own}"),
                    Ref(record), Parameters: [IdParameter(), ExpandParameter()], ClientPath: [name, "get"], Extensions: revision));
                foreach (var nav in table.Navigations.Values.Where(n => model.ById.TryGetValue(n.OtherTableId, out var o) && o.Def.Public.Read != PublicRead.None))
                {
                    var other = model.ById[nav.OtherTableId];
                    paths.Add(new OpenApiPathFragment($"/data/{name}/{{id}}/{nav.ApiName}", "get", $"{slug}_{name}_{nav.ApiName}",
                        $"{other.Def.PluralName ?? other.Def.DisplayName} of a {table.Def.DisplayName}",
                        Describe($"The {other.ApiName} records related to one {name} record through '{nav.ApiName}'."),
                        Ref($"{slug}_{other.ApiName}_page"), Parameters: [IdParameter(), Query("page", "integer"), Query("pageSize", "integer")],
                        ClientPath: [name, nav.ApiName], Extensions: revision));
                }
            }
            if (access.Create)
            {
                paths.Add(new OpenApiPathFragment($"/data/{name}", "post", $"{slug}_{name}_create", $"Create a {table.Def.DisplayName}",
                    Describe($"Create a {table.Def.DisplayName}. A signed-in visitor becomes its owner."),
                    Ref(record), RequestBodySchema: Ref(input), SuccessStatus: "201", ClientPath: [name, "create"], Extensions: revision));
            }
            if (access.UpdateOwn)
            {
                paths.Add(new OpenApiPathFragment($"/data/{name}/{{id}}", "patch", $"{slug}_{name}_update", $"Update your {table.Def.DisplayName}",
                    Describe($"Change fields of one of the signed-in visitor's own {label}. Send `version` to refuse a stale write."),
                    Ref(record), RequestBodySchema: Ref($"{input}_patch"), Parameters: [IdParameter()], ClientPath: [name, "update"], Extensions: revision));
            }
            if (access.DeleteOwn)
            {
                paths.Add(new OpenApiPathFragment($"/data/{name}/{{id}}", "delete", $"{slug}_{name}_delete", $"Delete your {table.Def.DisplayName}",
                    Describe($"Delete one of the signed-in visitor's own {label}."),
                    null, Parameters: [IdParameter()], SuccessStatus: "204", ClientPath: [name, "delete"], Extensions: revision));
            }
        }
        paths.Add(new OpenApiPathFragment("/_model", "get", $"{slug}_model", "The app's public model",
            Describe("The tables, fields and relationships this API exposes, from the published model."),
            new JsonObject { ["type"] = "object" }, ClientPath: ["model"], Extensions: revision));

        return new OpenApiFragment(instance.Name, $"{instance.Description}\n\n{model.Config.Settings.Description ?? manifest.Description}", paths, schemas);
    }

    private static JsonObject RecordSchema(RuntimeTable table, RuntimeModel model)
    {
        var properties = new JsonObject
        {
            ["id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            ["version"] = new JsonObject { ["type"] = "integer" },
            ["created_at"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            ["updated_at"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
        };
        foreach (var member in Visible(table))
        {
            properties[member.ApiName] = Nullable(ValueSchema(member, model));
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["description"] = table.Def.Description ?? table.Def.DisplayName,
            ["properties"] = properties,
            ["required"] = new JsonArray("id", "version", "created_at", "updated_at"),
        };
    }

    private static JsonObject InputSchema(RuntimeTable table, bool forCreate)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var member in Visible(table).Where(Writable))
        {
            properties[member.ApiName] = ValueSchema(member, null);
            if (forCreate && member.Required && member.Field?.Default is null)
            {
                required.Add(member.ApiName);
            }
        }
        if (!forCreate)
        {
            properties["version"] = new JsonObject { ["type"] = "integer", ["description"] = "The version you read; a stale one is refused with 409." };
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }
        return schema;
    }

    private static JsonObject ValueSchema(RuntimeMember member, RuntimeModel? model)
    {
        if (member.IsLookup)
        {
            var target = model?.ById.GetValueOrDefault(member.Lookup!.TargetTableId)?.ApiName;
            return new JsonObject
            {
                ["type"] = "string",
                ["format"] = "uuid",
                ["description"] = target is null ? "A record id." : $"The id of a {target} record (an object when expanded).",
            };
        }
        var field = member.Field!;
        JsonObject schema = field.Type switch
        {
            FieldType.Integer => new() { ["type"] = "integer" },
            FieldType.Decimal => new() { ["type"] = "number" },
            FieldType.Boolean => new() { ["type"] = "boolean" },
            FieldType.Date => new() { ["type"] = "string", ["format"] = "date" },
            FieldType.DateTime => new() { ["type"] = "string", ["format"] = "date-time" },
            FieldType.Email => new() { ["type"] = "string", ["format"] = "email" },
            FieldType.Url => new() { ["type"] = "string", ["format"] = "uri" },
            FieldType.Media => new() { ["type"] = "string", ["format"] = "uuid" },
            FieldType.Choice => new() { ["type"] = "string", ["enum"] = Options(member) },
            FieldType.MultiChoice => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Options(member) } },
            FieldType.Json => new(),
            _ => new() { ["type"] = "string" },
        };
        if (field.MaxLength is { } max)
        {
            schema["maxLength"] = max;
        }
        if (field.Minimum is { } min)
        {
            schema["minimum"] = min;
        }
        if (field.Maximum is { } most)
        {
            schema["maximum"] = most;
        }
        schema["description"] = field.Description ?? field.DisplayName;
        return schema;
    }

    private static JsonArray Options(RuntimeMember member) => new((member.Options ?? []).Select(o => (JsonNode)o).ToArray());

    private static JsonObject Nullable(JsonObject schema) => new() { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };

    private static JsonObject PageSchema(string record) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["items"] = new JsonObject { ["type"] = "array", ["items"] = Ref(record) },
            ["total"] = new JsonObject { ["type"] = "integer" },
            ["page"] = new JsonObject { ["type"] = "integer" },
            ["pageSize"] = new JsonObject { ["type"] = "integer" },
        },
        ["required"] = new JsonArray("items", "total", "page", "pageSize"),
    };

    private static JsonObject QuerySchema() => new()
    {
        ["type"] = "object",
        ["description"] = "filter: { and | or | not | field, op (eq ne gt gte lt lte in nin contains startsWith isNull), value }; sort: [{ field, direction }]",
        ["properties"] = new JsonObject
        {
            ["filter"] = new JsonObject { ["type"] = "object" },
            ["sort"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
            ["select"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["expand"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["search"] = new JsonObject { ["type"] = "string" },
            ["page"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            ["pageSize"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 200 },
        },
    };

    private static IReadOnlyList<JsonNode> ListParameters() =>
    [
        Query("page", "integer"), Query("pageSize", "integer"), Query("sort", "string"), Query("search", "string"),
        Query("select", "string"), Query("expand", "string"),
    ];

    private static JsonNode IdParameter() => new JsonObject
    {
        ["name"] = "id", ["in"] = "path", ["required"] = true, ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" },
    };

    private static JsonNode ExpandParameter() => Query("expand", "string");

    private static JsonNode Query(string name, string type) => new JsonObject
    {
        ["name"] = name, ["in"] = "query", ["required"] = false, ["schema"] = new JsonObject { ["type"] = type },
    };

    private static JsonObject Ref(string schema) => new() { ["$ref"] = $"#/components/schemas/{schema}" };

    private static string Camel(string name) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(name);
}
