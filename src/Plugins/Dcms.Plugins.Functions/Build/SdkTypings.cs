using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Data.Rls;

namespace Dcms.Plugins.Functions.Build;

/// <summary>
/// <c>@dcms/functions</c> for this instance's environment (ADR 0029): the static SDK plus what
/// is here — each Dynamic Apps app's published tables as typed <c>db.&lt;app&gt;.&lt;table&gt;</c>,
/// each app's contracts as <c>plugins.&lt;app&gt;.&lt;contract&gt;</c>. A build checks against it, so
/// code using a table that is gone fails to build. Types only: the broker still checks every
/// call, so stale types can never widen what a function reaches.
/// </summary>
public sealed class SdkTypings(IPluginContext context, PluginRegistry registry, PluginContextFactory instances)
{
    /// <param name="may">For a person reading it: whether they hold a permission. Tables need
    /// Dynamic Apps' model-read, an operation its own permission; null (a build) shows everything,
    /// since the broker and deploy-time vouching gate every call anyway.</param>
    public async Task<string> GenerateAsync(CancellationToken ct, Func<string, Task<bool>>? may = null)
    {
        using var rls = RlsScope.Environment(context.TenantId, context.EnvironmentId);
        var enabled = (await instances.EnabledInstancesAsync(context.TenantId, ct)).OrderBy(i => i.Slug, StringComparer.Ordinal).ToList();
        var (database, plugins, rows) = (new StringBuilder(), new StringBuilder(), new StringBuilder());
        var names = new HashSet<string>(StringComparer.Ordinal);

        var tables = may is null || await may("plugin:dynamic-apps:model-read");
        foreach (var app in enabled.Where(i => tables && i.PluginId == "dynamic-apps"))
        {
            var model = await context.Contracts.Get<IDynamicAppsAutomation>(app.InstanceId).ModelAsync(ct);
            if (model is null || model.Tables.Count == 0) continue;
            database.Append($"/** {Comment(app.Name)} */\n{Key(app.Slug)}: {{\n");
            foreach (var table in model.Tables.OrderBy(t => t.ApiName, StringComparer.Ordinal))
            {
                var row = Unique(names, Pascal(app.Slug) + Pascal(table.ApiName) + "Row");
                rows.Append(Row(row, table, model));
                database.Append($"  /** {Comment(table.DisplayName)} */\n  {Key(table.ApiName)}: Table<{row}>;\n");
            }
            database.Append("};\n");
        }

        foreach (var instance in enabled)
        {
            var contracts = registry.Contracts
                .Where(c => !c.Descriptor.IsPlatform && c.ProvidedBy(instance.PluginId) is not null)
                .Select(c => c.Descriptor)
                .Where(d => d.Operations.Any(o => o.Expose.HasFlag(OpExposure.Admin)))
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToList();
            var shown = new List<(ContractDescriptor Contract, List<OperationDescriptor> Operations)>();
            foreach (var contract in contracts)
            {
                var operations = new List<OperationDescriptor>();
                foreach (var op in contract.Operations.Where(o => o.Expose.HasFlag(OpExposure.Admin)))
                {
                    if (may is null || op.Permission is null || await may(op.Permission)) operations.Add(op);
                }
                if (operations.Count > 0) shown.Add((contract, operations));
            }
            if (shown.Count == 0) continue;
            plugins.Append($"/** {Comment(instance.Name)} */\n{Key(instance.Slug)}: {{\n");
            foreach (var (contract, operations) in shown)
            {
                plugins.Append($"  /** {Comment(contract.Description ?? contract.Id)} Declare \"{contract.Id}\" in uses.contracts. */\n");
                plugins.Append($"  {Camel(contract.Name)}: {{\n");
                foreach (var op in operations)
                {
                    var input = op.InputSchema is JsonObject { Count: > 0 } ? $"input: {Ts(op.InputSchema)}" : "";
                    var output = op.OutputSchema is null ? "void" : Ts(op.OutputSchema);
                    plugins.Append($"    /** {Comment(op.Description ?? op.Name)}{(op.Permission is null ? "" : $" Needs {op.Permission}.")} */\n");
                    plugins.Append($"    {Camel(op.Name)}({input}): Promise<{output}>;\n");
                }
                plugins.Append("  };\n");
            }
            plugins.Append("};\n");
        }

        return FunctionTypings.Generate(database.ToString().TrimEnd(), plugins.ToString().TrimEnd(), rows.ToString());
    }

    /// <summary>A strong ETag for what <see cref="GenerateAsync"/> returned.</summary>
    public static string ETag(string typings) =>
        "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(typings)))[..32] + "\"";

    private static string Row(string name, TableDef table, AppModel model)
    {
        var text = new StringBuilder($"/** A record of {Comment(table.DisplayName)}. */\nexport interface {name} {{\n");
        text.Append("  id: string;\n  version: number;\n  created_at: string;\n  updated_at: string;\n  created_by: string | null;\n  updated_by: string | null;\n");
        foreach (var field in table.Fields.Where(f => !f.Deprecated))
        {
            var choices = field.ChoiceSetId is { } set ? model.ChoiceSets.FirstOrDefault(c => c.Id == set)?.Options.Select(o => JsonSerializer.Serialize(o.Value)).ToList() : null;
            var type = field.Type switch
            {
                FieldType.Integer or FieldType.Decimal => "number",
                FieldType.Boolean => "boolean",
                FieldType.Json => "Json",
                FieldType.Choice when choices is { Count: > 0 } => string.Join(" | ", choices),
                FieldType.MultiChoice when choices is { Count: > 0 } => $"Array<{string.Join(" | ", choices)}>",
                FieldType.MultiChoice => "string[]",
                _ => "string",
            };
            text.Append($"  /** {Comment(field.DisplayName)}{(field.ReadOnly ? " (read-only to members)" : "")} */\n  {Key(field.ApiName)}: {type} | null;\n");
        }
        foreach (var lookup in model.Relationships.Where(r => r.SourceTableId == table.Id && r.Kind != RelationshipKind.ManyToMany))
        {
            text.Append($"  /** The id of the linked record: {Comment(lookup.DisplayName ?? lookup.ApiName)}. */\n  {Key(lookup.ApiName)}: string | null;\n");
        }
        return text.Append("}\n\n").ToString();
    }

    /// <summary>A TypeScript type for a contract's JSON Schema: enough to guide, never to check.</summary>
    private static string Ts(JsonNode? schema, int depth = 0)
    {
        if (schema is not JsonObject s || depth > 6) return "Json";
        if (s["enum"] is JsonArray values) return string.Join(" | ", values.Select(v => v?.ToJsonString() ?? "null"));
        if ((s["anyOf"] ?? s["oneOf"]) is JsonArray any) return string.Join(" | ", any.Select(a => Ts(a, depth + 1)).Distinct());
        var types = s["type"] switch
        {
            JsonArray many => many.Select(t => t?.GetValue<string>() ?? "").ToList(),
            JsonValue one => [one.GetValue<string>()],
            _ => [],
        };
        if (types.Count == 0) return "Json";
        return string.Join(" | ", types.Distinct().Select(type => type switch
        {
            "null" => "null",
            "string" => "string",
            "integer" or "number" => "number",
            "boolean" => "boolean",
            "array" => $"Array<{Ts(s["items"], depth + 1)}>",
            "object" when s["properties"] is JsonObject props && props.Count > 0 => "{ " + string.Join(" ", props.Select(p =>
                $"{Key(p.Key)}{((s["required"] as JsonArray)?.Any(r => r?.GetValue<string>() == p.Key) == true ? "" : "?")}: {Ts(p.Value, depth + 1)};")) + " }",
            _ => "{ [key: string]: Json }",
        }));
    }

    private static string Unique(HashSet<string> taken, string name)
    {
        var candidate = name;
        for (var i = 2; !taken.Add(candidate); i++) candidate = name + i;
        return candidate;
    }

    /// <summary><c>dynamic-apps.records</c> → <c>DynamicAppsRecords</c>.</summary>
    private static string Pascal(string name)
    {
        var pascal = string.Concat(name.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return pascal.Length > 0 && char.IsLetter(pascal[0]) ? pascal : "T" + pascal;
    }

    private static string Camel(string name) => Pascal(name) is var p ? char.ToLowerInvariant(p[0]) + p[1..] : name;

    /// <summary>A property name, quoted unless it is a plain identifier.</summary>
    private static string Key(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
            ? name
            : JsonSerializer.Serialize(name);

    /// <summary>Text safe inside a comment: tenant-chosen names cannot close it.</summary>
    private static string Comment(string text) => text.Replace("*/", "* /", StringComparison.Ordinal).Replace('\n', ' ');
}
