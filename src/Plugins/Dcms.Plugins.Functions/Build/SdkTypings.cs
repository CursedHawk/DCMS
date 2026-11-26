using Dcms.PluginSdk.Runtime.Typing;
using Dcms.PluginSdk.Abstractions;
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
using Dcms.Plugins.WebSockets.Api;

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
    /// <summary>An app with published tables, as the SDK shows it.</summary>
    public sealed record App(string Slug, string Name, AppModel Model);

    /// <summary>An instance's contracts, with the admin operations shown.</summary>
    public sealed record Instance(string Slug, string Name, IReadOnlyList<(ContractDescriptor Contract, List<OperationDescriptor> Operations)> Contracts);

    /// <summary>A WebSockets app's channels, typed in <c>Channels</c> and for <c>on.websocket</c> (ADR 0037).</summary>
    public sealed record ChannelSet(string Slug, string Name, IReadOnlyList<ChannelDefinition> Channels);

    /// <summary>What both halves of the SDK are generated from (ADR 0029, 0030, 0037).</summary>
    public sealed record Model(IReadOnlyList<App> Apps, IReadOnlyList<Instance> Instances, IReadOnlyList<ChannelSet>? ChannelSets = null);

    /// <param name="may">For a person reading it: whether they hold a permission. Tables need
    /// Dynamic Apps' model-read, an operation its own permission; null (a build) shows everything,
    /// since the broker and deploy-time vouching gate every call anyway.</param>
    public async Task<string> GenerateAsync(CancellationToken ct, Func<string, Task<bool>>? may = null) =>
        TypeScript(await ModelAsync(ct, may));

    /// <summary>The C# half: Sdk.g.cs.</summary>
    public async Task<string> GenerateCSharpAsync(CancellationToken ct, Func<string, Task<bool>>? may = null) =>
        CSharp(await ModelAsync(ct, may));

    public async Task<Model> ModelAsync(CancellationToken ct, Func<string, Task<bool>>? may = null)
    {
        using var rls = RlsScope.Environment(context.TenantId, context.EnvironmentId);
        var enabled = (await instances.EnabledInstancesAsync(context.TenantId, ct)).OrderBy(i => i.Slug, StringComparer.Ordinal).ToList();
        var apps = new List<App>();
        foreach (var app in enabled.Where(i => i.PluginId == "dynamic-apps"))
        {
            // Plugin permissions are held per instance: the apps whose model the member may read.
            if (may is not null && !await may(PluginPermissions.ForInstance("plugin:dynamic-apps:model-read", app.Slug))) continue;
            var model = await context.Contracts.Get<IDynamicAppsAutomation>(app.InstanceId).ModelAsync(ct);
            if (model is null || model.Tables.Count == 0) continue;
            apps.Add(new App(app.Slug, app.Name, model));
        }

        var shown = new List<Instance>();
        foreach (var instance in enabled)
        {
            var contracts = registry.Contracts
                .Where(c => !c.Descriptor.IsPlatform && c.ProvidedBy(instance.PluginId) is not null)
                .Select(c => c.Descriptor)
                .Where(d => d.Operations.Any(o => o.Expose.HasFlag(OpExposure.Admin)))
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .ToList();
            var visible = new List<(ContractDescriptor Contract, List<OperationDescriptor> Operations)>();
            // The instance's own narrower schemas (a WebSockets instance's channels, ADR 0037).
            var refined = registry.FindPlugin(instance.PluginId)?.OperationInputSchemas(instance)
                ?? new Dictionary<string, JsonNode>();
            foreach (var contract in contracts)
            {
                var operations = new List<OperationDescriptor>();
                foreach (var op in contract.Operations.Where(o => o.Expose.HasFlag(OpExposure.Admin)))
                {
                    if (may is not null && op.Permission is not null && !await may(PluginPermissions.Resolve(instance.PluginId, instance.Slug, op.Permission))) continue;
                    operations.Add(refined.TryGetValue($"{contract.Name}.{op.Name}", out var input) ? op with { InputSchema = input } : op);
                }
                if (operations.Count > 0) visible.Add((contract, operations));
            }
            if (visible.Count > 0) shown.Add(new Instance(instance.Slug, instance.Name, visible));
        }
        var channelSets = new List<ChannelSet>();
        foreach (var ws in enabled.Where(i => i.PluginId == "websockets"))
        {
            if (may is not null && !await may(PluginPermissions.ForInstance(WebSocketsPermissions.Read, ws.Slug))) continue;
            var channels = ChannelConfig.Read(ws.Config.RootElement);
            if (channels.Count > 0) channelSets.Add(new ChannelSet(ws.Slug, ws.Name, channels));
        }
        return new Model(apps, shown, channelSets);
    }

    /// <summary><c>@dcms/functions</c>'s declarations for a TypeScript function.</summary>
    public static string TypeScript(Model model)
    {
        var (database, plugins, rows) = (new StringBuilder(), new StringBuilder(), new StringBuilder());
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in model.Apps)
        {
            database.Append($"/** {Comment(app.Name)} */\n{Key(app.Slug)}: {{\n");
            foreach (var table in app.Model.Tables.OrderBy(t => t.ApiName, StringComparer.Ordinal))
            {
                var row = Unique(names, Pascal(app.Slug) + Pascal(table.ApiName) + "Row");
                rows.Append(Row(row, table, app.Model));
                database.Append($"  /** {Comment(table.DisplayName)} */\n  {Key(table.ApiName)}: Table<{row}>;\n");
            }
            database.Append("};\n");
        }
        foreach (var instance in model.Instances)
        {
            plugins.Append($"/** {Comment(instance.Name)} */\n{Key(instance.Slug)}: {{\n");
            foreach (var (contract, operations) in instance.Contracts)
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
        var channels = new StringBuilder();
        foreach (var set in model.ChannelSets ?? [])
        {
            channels.Append($"/** {Comment(set.Name)} */\n{Key(set.Slug)}: {{\n");
            foreach (var channel in set.Channels)
            {
                if (channel.Description is { Length: > 0 } d) channels.Append($"  /** {Comment(d)} */\n");
                channels.Append($"  {Key(channel.Name)}: {Ts(channel.Schema is { } s ? JsonNode.Parse(s.GetRawText()) : null)};\n");
            }
            channels.Append("};\n");
        }
        return FunctionTypings.Generate(database.ToString().TrimEnd(), plugins.ToString().TrimEnd(), rows.ToString(), channels.ToString().TrimEnd());
    }

    /// <summary>
    /// Sdk.g.cs, the workspace's half of the C# SDK (ADR 0030): <c>ctx.Db.&lt;App&gt;.&lt;Table&gt;</c> over
    /// typed row records and <c>ctx.Plugins.&lt;App&gt;.&lt;Contract&gt;.&lt;Operation&gt;()</c>, as C# 14
    /// extension properties on the static half's <c>Context</c>. Contract operations take and give
    /// generated records (and, as before, plain JSON).
    /// </summary>
    public static string CSharp(Model model)
    {
        // The static half's own type names, which a generated one must not take.
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "Context", "Function", "Table", "Query", "Filter", "Sort", "Page", "Row", "Logger", "Access", "Database", "Plugins", "WorkspaceSdk",
            "HttpRequest", "HttpResponse", "HttpResult", "HttpResultBody", "User", "TriggerInfo", "RunInfo", "HostException",
            "EmailMessage", "Notification", "ContentPage", "RowEvent", "RelationEvent", "PlatformEvents", "WebSocketMessage",
        };
        var text = new StringBuilder("""
            // Sdk.g.cs: generated for this workspace's environment. Do not edit: it is rebuilt.
            #nullable enable
            namespace Dcms.Functions;

            public static class WorkspaceSdk
            {
                extension(Context ctx)
                {
                    /// <summary>This environment's Dynamic Apps tables. Each needs [UsesTable("app.table", verbs…)].</summary>
                    public Database Db => new(ctx);
                    /// <summary>Other apps' contracts. Each needs [UsesContracts("name@major")].</summary>
                    public Plugins Plugins => new(ctx);
                }
            }

            """);
        var types = new StringBuilder();

        text.Append("public sealed class Database(Context ctx)\n{\n");
        foreach (var app in model.Apps)
        {
            var appType = Unique(names, Pascal(app.Slug) + "App");
            text.Append($"    /// <summary>{Xml(app.Name)}</summary>\n    public {appType} {Pascal(app.Slug)} => new(ctx);\n");
            types.Append($"public sealed class {appType}(Context ctx)\n{{\n");
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var table in app.Model.Tables.OrderBy(t => t.ApiName, StringComparer.Ordinal))
            {
                var row = Unique(names, Pascal(app.Slug) + Pascal(table.ApiName) + "Row");
                types.Append($"    /// <summary>{Xml(table.DisplayName)}: \"{Xml(app.Slug)}.{Xml(table.ApiName)}\".</summary>\n");
                types.Append($"    public Table<{row}> {Unique(members, Pascal(table.ApiName))} => ctx.Table<{row}>({Literal(app.Slug)}, {Literal(table.ApiName)});\n");
                RowRecord(types, row, table, app.Model);
            }
            types.Append("}\n\n");
        }
        text.Append("}\n\npublic sealed class Plugins(Context ctx)\n{\n");
        foreach (var instance in model.Instances)
        {
            var instanceType = Unique(names, Pascal(instance.Slug) + "Plugin");
            text.Append($"    /// <summary>{Xml(instance.Name)}</summary>\n    public {instanceType} {Pascal(instance.Slug)} => new(ctx);\n");
            types.Append($"public sealed class {instanceType}(Context ctx)\n{{\n");
            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (contract, operations) in instance.Contracts)
            {
                var contractType = Unique(names, Pascal(instance.Slug) + Pascal(contract.Name) + "Contract");
                types.Append($"    /// <summary>{Xml(contract.Description ?? contract.Id)} Needs [UsesContracts(\"{Xml(contract.Id)}\")].</summary>\n");
                types.Append($"    public {contractType} {Unique(members, Pascal(contract.Name))} => new(ctx);\n");
                var ops = new StringBuilder($"public sealed class {contractType}(Context ctx)\n{{\n");
                var opNames = new HashSet<string>(StringComparer.Ordinal);
                var records = new JsonSchemaCs(names, types);
                foreach (var op in operations)
                {
                    var method = Unique(opNames, Pascal(op.Name));
                    var summary = $"    /// <summary>{Xml(op.Description ?? op.Name)}{(op.Permission is null ? "" : $" Needs {Xml(op.Permission)}.")}</summary>\n";
                    var call = $"ctx.Contract<§>({Literal(instance.Slug)}, {Literal(contract.Name)}, {Literal(op.Name)}, input)";

                    // Typed (ADR 0037): the input and the result as records, one overload per
                    // variant of a union input (a WebSockets instance's channels).
                    var hint = Pascal(instance.Slug) + Pascal(contract.Name) + Pascal(op.Name);
                    var output = op.OutputSchema is null ? null : records.Type(op.OutputSchema, hint + "Result");
                    IReadOnlyList<string> inputs = op.InputSchema is not JsonObject { Count: > 0 } ? []
                        : records.Branches(op.InputSchema, hint) ?? [records.Type(op.InputSchema, hint + "Input")];
                    foreach (var input in inputs.Where(i => i != "JsonNode"))
                    {
                        ops.Append(summary);
                        ops.Append(output is null
                            ? $"    public async Task {method}({input} input) => await {call.Replace("§", "JsonNode")};\n"
                            // TOut? of an unconstrained T is no Nullable<T>: name the nullable type itself.
                            : $"    public Task<{output}?> {method}({input} input) => {call.Replace("§", output + "?")};\n");
                    }

                    // Untyped, as before: what code written against JSON keeps calling.
                    ops.Append(summary);
                    if (op.InputSchema is JsonObject { Count: > 0 }) ops.Append($"    /// <param name=\"input\">{Xml(Ts(op.InputSchema))}</param>\n");
                    if (op.OutputSchema is not null) ops.Append($"    /// <returns>{Xml(Ts(op.OutputSchema))}</returns>\n");
                    ops.Append($"    public Task<JsonNode?> {method}(object? input = null) =>\n");
                    ops.Append($"        {call.Replace("§", "JsonNode")};\n");
                }
                types.Insert(0, ops.Append("}\n\n").ToString());
            }
            types.Append("}\n\n");
        }
        // Each channel's payload as a record, for WebSocketMessage<TPayload> (ADR 0037).
        var payloads = new JsonSchemaCs(names, types);
        foreach (var set in model.ChannelSets ?? [])
        {
            foreach (var channel in set.Channels.Where(c => c.Schema is not null))
            {
                payloads.Type(JsonNode.Parse(channel.Schema!.Value.GetRawText()), Pascal(set.Slug) + Pascal(channel.Name) + "Payload");
            }
        }
        return text.Append("}\n\n").Append(types).ToString();
    }

    private static void RowRecord(StringBuilder types, string name, TableDef table, AppModel model)
    {
        // Every column nullable: a row read back has them all, a row to write has only what to set.
        var text = new StringBuilder($"/// <summary>A record of {Xml(table.DisplayName)}.</summary>\npublic sealed record {name} : Row\n{{\n");
        var members = new HashSet<string>(StringComparer.Ordinal) { name, "Id", "Version", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "EqualityContract" };
        foreach (var field in table.Fields.Where(f => !f.Deprecated))
        {
            var choices = field.ChoiceSetId is { } set ? model.ChoiceSets.FirstOrDefault(c => c.Id == set)?.Options.Select(o => o.Value).ToList() : null;
            var type = field.Type switch
            {
                FieldType.Integer => "long?",
                FieldType.Decimal => "decimal?",
                FieldType.Boolean => "bool?",
                FieldType.Json => "JsonNode?",
                FieldType.MultiChoice => "IReadOnlyList<string>?",
                _ => "string?",
            };
            var doc = Xml(field.DisplayName) + (field.ReadOnly ? " (read-only to members)" : "")
                + (choices is { Count: > 0 } ? ": " + Xml(string.Join(", ", choices.Select(c => JsonSerializer.Serialize(c)))) : "");
            text.Append($"    /// <summary>{doc}</summary>\n");
            text.Append($"    [System.Text.Json.Serialization.JsonPropertyName({Literal(field.ApiName)})] public {type} {Unique(members, Pascal(field.ApiName))} {{ get; init; }}\n");
        }
        foreach (var lookup in model.Relationships.Where(r => r.SourceTableId == table.Id && r.Kind != RelationshipKind.ManyToMany))
        {
            text.Append($"    /// <summary>The id of the linked record: {Xml(lookup.DisplayName ?? lookup.ApiName)}.</summary>\n");
            text.Append($"    [System.Text.Json.Serialization.JsonPropertyName({Literal(lookup.ApiName)})] public string? {Unique(members, Pascal(lookup.ApiName))} {{ get; init; }}\n");
        }
        types.Insert(0, text.Append("}\n\n").ToString());
    }

    /// <summary>
    /// Text safe inside a <c>///</c> doc comment: tenant-chosen names cannot end the line, and with
    /// it the comment. C# ends a line at U+0085, U+2028 and U+2029 too, so a name such as an app's
    /// or a field's (which a member with no say over the code may set) could otherwise put code into
    /// Sdk.g.cs, compiled with every C# function and run with its deployer's rights.
    /// </summary>
    private static string Xml(string text) => System.Security.SecurityElement.Escape(OneLine(text)) ?? "";

    private static string OneLine(string text) =>
        new(text.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator ? ' ' : c).ToArray());

    /// <summary>A C# string literal of anything: JSON's string escapes are all C#'s too.</summary>
    private static string Literal(string text) => JsonSerializer.Serialize(text);

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

    private static readonly JsonSchemaTs Typer = new("Json");

    /// <summary>A TypeScript type for a contract's JSON Schema: enough to guide, never to check.</summary>
    private static string Ts(JsonNode? schema) => Typer.Type(schema);

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
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..])
            .Select(p => string.Concat(p.Where(c => char.IsLetterOrDigit(c) || c == '_'))));
        return pascal.Length > 0 && char.IsLetter(pascal[0]) ? pascal : "T" + pascal;
    }

    private static string Camel(string name) => Pascal(name) is var p ? char.ToLowerInvariant(p[0]) + p[1..] : name;

    /// <summary>A property name, quoted unless it is a plain identifier.</summary>
    private static string Key(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
            ? name
            : JsonSerializer.Serialize(name);

    /// <summary>Text safe inside a comment: tenant-chosen names cannot close it.</summary>
    private static string Comment(string text) => OneLine(text).Replace("*/", "* /", StringComparison.Ordinal);
}
