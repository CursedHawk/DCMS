using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;

namespace Dcms.Plugins.DynamicApps.Automation;

/// <summary>
/// TypeScript declarations of what a flow script's <c>ctx</c> holds (ADR 0025), from the app's
/// model and the flow as it stands: the trigger table's record, the outputs of the steps before
/// the script, typed from each action's output schema. The one generator — the flow editor's
/// IntelliSense and the assistant both read it — and only ever a guide: the server strips types,
/// it does not check them.
/// </summary>
public static class FlowTypings
{
    /// <param name="atStepId">The script step being written: only the steps before it are in <c>ctx.steps</c>. Null for the whole flow.</param>
    public static string Generate(AppConfig config, FlowDef flow, string? atStepId, IReadOnlyList<IFlowAction> actions)
    {
        var rows = new SortedDictionary<string, TableDef>(StringComparer.Ordinal);
        string RowOf(TableDef table)
        {
            var name = TypeName(table.ApiName);
            rows[name] = table;
            return name;
        }

        var trigger = flow.Trigger.Event.StartsWith("row.", StringComparison.Ordinal)
            ? config.Tables.FirstOrDefault(t => t.Id == flow.Trigger.TableId)
            : null;
        var row = trigger is null ? "null" : RowOf(trigger);
        var previous = trigger is not null && flow.Trigger.Event == Api.AppEvent.RowUpdated ? $"Partial<{row}>" : "null";

        var steps = new StringBuilder();
        foreach (var step in flow.Steps.TakeWhile(s => s.Id != atStepId))
        {
            var action = actions.FirstOrDefault(a => ActionCatalog.Key(a) == step.Action);
            var table = step.Input["table"] is JsonValue v && v.TryGetValue<string>(out var t) ? config.Tables.FirstOrDefault(x => x.ApiName == t) : null;
            var type = action?.OutputSchema is { } schema ? TypeOf(schema, table, RowOf) : "unknown";
            // A step skipped by its condition leaves null.
            var skippable = string.IsNullOrWhiteSpace(step.Condition) || type.Split(" | ").Contains("null") ? "" : " | null";
            steps.Append(CultureInfo.InvariantCulture, $"    /** {Comment(step.Action)} */\n    {Key(step.Id)}: {type}{skippable};\n");
        }

        var script = atStepId is null ? null : flow.Steps.FirstOrDefault(s => s.Id == atStepId);
        var args = script?.Input["args"] is JsonObject declared && declared.Count > 0
            ? "{ " + string.Join(" ", declared.Select(p => $"{Key(p.Key)}: FlowJson;")) + " }"
            : "{ [key: string]: FlowJson }";

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"""
            // What ctx holds in flow '{Comment(flow.ApiName)}'{(atStepId is null ? "" : $", step '{Comment(atStepId)}'")}. Generated from the draft; do not edit.
            // A guide for the editor only: scripts run with their types stripped, never checked.

            type FlowJson = string | number | boolean | null | FlowJson[] | {"{"} [key: string]: FlowJson {"}"};

            """);
        text.Append('\n');
        foreach (var (name, table) in rows)
        {
            text.Append(CultureInfo.InvariantCulture, $"/** A record of {Comment(table.DisplayName)} ({Comment(table.ApiName)}). */\ninterface {name} {{\n");
            text.Append("""
                  id: string;
                  version: number;
                  created_at: string;
                  updated_at: string;
                  created_by: string | null;
                  updated_by: string | null;

                """);
            foreach (var field in table.Fields)
            {
                text.Append(CultureInfo.InvariantCulture, $"  /** {Comment(field.DisplayName)} */\n  {Key(field.ApiName)}: {FieldType(field.Type)} | null;\n");
            }
            foreach (var lookup in config.Relationships.Where(r => r.SourceTableId == table.Id && r.Kind != RelationshipKind.ManyToMany))
            {
                var target = config.Tables.FirstOrDefault(x => x.Id == lookup.TargetTableId)?.DisplayName ?? "related";
                text.Append(CultureInfo.InvariantCulture, $"  /** The id of the {Comment(target)} record. */\n  {Key(lookup.ApiName)}: string | null;\n");
            }
            text.Append("}\n\n");
        }

        text.Append(CultureInfo.InvariantCulture, $$"""
            /** What a flow script's main(ctx) receives. */
            interface FlowContext {
              /** The event that started the run. */
              event: {
                eventName: string;
                eventId: string;
                occurredAt: string;
                revision: number;
                entity: { type: string; id: string | null } | null;
                payload: { [key: string]: FlowJson };
                changedFields: string[];
                correlationId: string;
                causationId: string | null;
                depth: number;
              };
              /** The record the event is about{{(trigger is null ? " — none for this trigger" : "")}}. */
              row: {{row}};
              /** row.updated only: the changed fields' values before the change. */
              previous: {{previous}};
              /** The api names of the fields whose value changed (row.updated). */
              changedFields: string[];
              /** A manual run's input, or that of the flow that invoked this one. */
              input: { [key: string]: FlowJson } | null;
              /** The outputs of the steps before this one. */
              steps: {
            {{steps}}  };
              /** This run. */
              run: { id: string; flow: string; revision: number; depth: number };
              /** This step's `args`, their templates rendered. */
              args: {{args}};
            }

            """);
        return text.ToString();
    }

    /// <summary>A TypeScript type for a JSON Schema, as far as the flow editor needs one.</summary>
    private static string TypeOf(JsonNode? schema, TableDef? table, Func<TableDef, string> rowOf)
    {
        if (schema is not JsonObject s)
        {
            return "unknown";
        }
        if (s["x-dcms-record"] is JsonValue r && r.TryGetValue<bool>(out var isRecord) && isRecord)
        {
            // The table named by a template is not known until the run.
            return table is null ? "{ id: string; version: number; [key: string]: FlowJson }" : rowOf(table);
        }
        if (s["anyOf"] is JsonArray any)
        {
            return string.Join(" | ", any.Select(a => TypeOf(a, table, rowOf)).Distinct());
        }
        if (s["enum"] is JsonArray values)
        {
            return string.Join(" | ", values.Select(v => v?.ToJsonString() ?? "null"));
        }
        return s["type"]?.GetValue<string>() switch
        {
            "null" => "null",
            "string" => "string",
            "integer" or "number" => "number",
            "boolean" => "boolean",
            "array" => $"Array<{TypeOf(s["items"], table, rowOf)}>",
            "object" when s["properties"] is JsonObject props && props.Count > 0 => "{ " + string.Join(" ", props.Select(p =>
                $"{Key(p.Key)}{((s["required"] as JsonArray)?.Any(x => x?.GetValue<string>() == p.Key) == true ? "" : "?")}: {TypeOf(p.Value, table, rowOf)};")) + " }",
            "object" => "{ [key: string]: FlowJson }",
            _ => "unknown",
        };
    }

    private static string FieldType(FieldType type) => type switch
    {
        Api.Model.FieldType.Integer or Api.Model.FieldType.Decimal => "number",
        Api.Model.FieldType.Boolean => "boolean",
        Api.Model.FieldType.MultiChoice => "string[]",
        Api.Model.FieldType.Json => "FlowJson",
        _ => "string",
    };

    /// <summary><c>deal_stage</c> → <c>DealStageRow</c>: never a global TypeScript already has (a table named <c>date</c>).</summary>
    private static string TypeName(string apiName) =>
        string.Concat(apiName.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..])) + "Row";

    /// <summary>A property name, quoted unless it is a plain identifier.</summary>
    private static string Key(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
            ? name
            : System.Text.Json.JsonSerializer.Serialize(name);

    /// <summary>Text safe inside a comment: tenant-chosen names cannot close it.</summary>
    private static string Comment(string text) => text.Replace("*/", "* /", StringComparison.Ordinal).Replace('\n', ' ');
}
