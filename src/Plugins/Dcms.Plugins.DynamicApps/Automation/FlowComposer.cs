using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;

namespace Dcms.Plugins.DynamicApps.Automation;

/// <param name="Request">What the member wants the flow to do, in their own words.</param>
/// <param name="Flow">The flow as the editor holds it now, unsaved: what a follow-up request changes.</param>
public sealed record FlowComposeRequest(string Request, FlowDef? Flow = null);

/// <param name="Note">The model's word to the member: what the flow does, and what to check.</param>
/// <param name="Issues">What validation still says about it; the editor shows them, nothing is saved.</param>
public sealed record FlowComposition(FlowDef Flow, string? Note, IReadOnlyList<ConfigIssue> Issues);

/// <summary>
/// "Write with AI" in the flow editor: one request in a member's words becomes a whole flow,
/// written by the workspace's own AI provider (<see cref="IPluginAi"/>, its quota and audit)
/// against this app's tables and action catalog, then resolved and validated exactly as a saved
/// change would be. Nothing is saved — the editor fills its form, and the member reviews and
/// saves it like any other edit. An answer the server refuses goes back to the model once.
/// </summary>
public sealed class FlowComposer(IPluginContext context, ConfigurationService configuration)
{
    public const int MaxRequestChars = 2_000;
    private const int Attempts = 2;

    /// <exception cref="ContractValidationException">The request is empty or too long, or no AI provider answered.</exception>
    public async Task<FlowComposition> ComposeAsync(FlowComposeRequest input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Request) || input.Request.Length > MaxRequestChars)
        {
            throw new ContractValidationException($"Describe the flow in 1 to {MaxRequestChars:N0} characters.");
        }
        var config = (await configuration.GetDraftAsync(ct) ?? await configuration.GetPublishedAsync(ct))?.Config ?? new AppConfig();
        return await ComposeAsync(config, await ActionCatalog.WithProvidersAsync(context, ct), input, context.Contracts.Get<IPluginAi>(), ct);
    }

    /// <summary>The composition itself, against a given model and catalog: what the tests drive with a scripted model.</summary>
    public static async Task<FlowComposition> ComposeAsync(
        AppConfig config, IReadOnlyList<IFlowAction> actions, FlowComposeRequest input, IPluginAi ai, CancellationToken ct)
    {
        var system = SystemPrompt(config, actions);
        var prompt = UserPrompt(config, input);

        string? refused = null;
        FlowComposition? last = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var completion = await ai.CompleteAsync(new AiCompletionRequest(prompt, system, PluginAiTokens), ct);
            var text = completion.Text ?? throw new ContractValidationException(
                "No AI answered: this workspace has no AI provider set up, or it is unavailable.");
            try
            {
                last = Build(config, input.Flow, Answer(text), actions);
                refused = string.Join("; ", last.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => $"{i.Path}: {i.Message}"));
            }
            catch (Exception e) when (e is JsonException or ContractValidationException or InvalidOperationException)
            {
                refused = e.Message;
            }
            if (string.IsNullOrEmpty(refused))
            {
                break;
            }
            prompt = $"{UserPrompt(config, input)}\n\nYour previous answer:\n{text}\n\nThe server refused it: {refused}\nAnswer again with the whole corrected JSON object.";
        }
        return last ?? throw new ContractValidationException($"The AI's flow could not be used: {refused}");
    }

    private const int PluginAiTokens = 4_000;

    /// <summary>The JSON object in a model's answer, without the code fence some models add anyway.</summary>
    private static JsonObject Answer(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("The answer held no JSON object.");
        }
        return JsonNode.Parse(text[start..(end + 1)]) as JsonObject ?? throw new InvalidOperationException("The answer is not a JSON object.");
    }

    /// <summary>The answer as a flow: resolved by the change applier and checked by the validator, on a copy of the draft.</summary>
    private static FlowComposition Build(AppConfig config, FlowDef? editing, JsonObject answer, IReadOnlyList<IFlowAction> actions)
    {
        var note = answer["note"] is JsonValue n && n.TryGetValue<string>(out var said) ? said : null;
        var value = new JsonObject();
        foreach (var key in (string[])["apiName", "displayName", "description", "trigger", "condition", "steps"])
        {
            value[key] = answer[key]?.DeepClone();
        }
        // Every trigger key present, so a changed flow loses what the new trigger does not use.
        var trigger = value["trigger"] as JsonObject ?? throw new InvalidOperationException("The flow has no trigger.");
        trigger["tableId"] ??= null;
        trigger["changedFields"] ??= new JsonArray();
        trigger["everyMinutes"] ??= null;

        var existing = editing is null ? null : config.Flows.FirstOrDefault(f => f.Id == editing.Id);
        string apiName;
        ChangeOperation operation;
        if (existing is not null)
        {
            // A saved flow keeps its api name: other flows and the site may invoke it by that name.
            apiName = existing.ApiName;
            value["apiName"] = apiName;
            operation = new ChangeOperation { Op = "update", Type = "flow", Target = existing.Id.ToString(), Value = value };
        }
        else
        {
            var wanted = value["apiName"] is JsonValue a && a.TryGetValue<string>(out var proposed) && proposed.Length > 0 ? proposed : "ai_flow";
            apiName = wanted;
            for (var i = 2; config.Flows.Any(f => f.ApiName == apiName); i++)
            {
                apiName = $"{wanted}_{i}";
            }
            value["apiName"] = apiName;
            operation = new ChangeOperation { Op = "create", Type = "flow", Value = value };
        }

        var applied = ChangeApplier.Apply(config, [operation]);
        var flow = applied.Flows.First(f => f.ApiName == apiName);
        var path = $"flows.{apiName}";
        var issues = ConfigValidator.Validate(applied, actions: actions.Select(ActionCatalog.Key).ToHashSet(StringComparer.Ordinal))
            .Where(i => i.Path == path || i.Path.StartsWith(path + ".", StringComparison.Ordinal))
            .ToList();
        return new FlowComposition(flow, note, issues);
    }

    private static string SystemPrompt(AppConfig config, IReadOnlyList<IFlowAction> actions)
    {
        var text = new StringBuilder();
        text.Append($$"""
            You write one automation flow for a DCMS Dynamic Apps application. Answer with ONLY a JSON object — no prose, no code fence:
            { "apiName": "snake_case", "displayName": "a short label",
              "trigger": { "event": "…", "tableId": "<table api name; row.* events only>", "changedFields": ["<field api name>"],
                           "everyMinutes": <schedule only> },
              "condition": "<expression, or null for always>",
              "steps": [ { "id": "snake_case", "action": "<key from the catalog, e.g. records.create@1>", "input": { … }, "condition": "<optional expression>" } ],
              "note": "<one or two sentences to the person: what the flow does, and anything they must check or fill in>" }

            Trigger events: row.created, row.updated (changedFields narrows it; empty means any change), row.deleted — each with tableId;
            schedule (everyMinutes {{AutomationLimits.MinScheduleMinutes}}–{{AutomationLimits.MaxScheduleMinutes}}); manual (started by a person or by flow.invoke@1);
            flow.event.<name> (announced by event.publish@1); visitor.registered, form.submitted, user.invited, user.activated.
            Do not use relation.* triggers.

            {{FlowLanguageText.Expressions}}
            Names: {{string.Join(", ", ConfigValidator.FlowVariables)}}.
            Functions: {{string.Join("; ", Expressions.FunctionDocs.Select(f => $"{f.Value.Signature} — {f.Value.Description}"))}}
            {{FlowLanguageText.Templates}}
            {{FlowLanguageText.Scripts}}

            Use only the tables, fields and actions below — never invent one. Prefer catalog actions; use script.run@1 only for
            computation they cannot express. Records actions name a table by its api name in "table" and fields by api name in "values".
            Where something the person must decide is unknown (an email address, a threshold), pick a sensible placeholder and say so in note.

            The app's tables (api name, label; fields as api name: type):

            """);
        foreach (var table in config.Tables)
        {
            var members = table.Fields.Select(f => $"{f.ApiName}: {JsonNamingPolicy.CamelCase.ConvertName(f.Type.ToString())}{(f.Required ? " (required)" : "")}")
                .Concat(config.Relationships.Where(r => r.SourceTableId == table.Id && r.Kind != RelationshipKind.ManyToMany)
                    .Select(r => $"{r.ApiName}: lookup → {config.Tables.FirstOrDefault(t => t.Id == r.TargetTableId)?.ApiName}"));
            text.Append($"- {table.ApiName} \"{table.DisplayName}\": {string.Join(", ", members)}\n");
        }
        if (config.Flows.Count > 0)
        {
            text.Append($"\nExisting flows (api name, trigger): {string.Join(", ", config.Flows.Select(f => $"{f.ApiName} ({f.Trigger.Event})"))}\n");
        }
        text.Append("\nThe action catalog (key, risk: description; input JSON Schema):\n");
        foreach (var action in actions)
        {
            text.Append($"- {ActionCatalog.Key(action)}, {action.Risk}: {action.Description} Input: {action.InputSchema.ToJsonString()}\n");
        }
        return text.ToString();
    }

    private static string UserPrompt(AppConfig config, FlowComposeRequest input)
    {
        var prompt = $"The person asks:\n{input.Request.Trim()}";
        if (input.Flow is { Steps.Count: > 0 } flow)
        {
            // Names rather than ids, as the model is asked to answer in.
            var table = config.Tables.FirstOrDefault(t => t.Id == flow.Trigger.TableId);
            var current = JsonSerializer.SerializeToNode(flow, ConfigJson.Options)!.AsObject();
            current.Remove("id");
            if (current["trigger"] is JsonObject trigger)
            {
                trigger["tableId"] = table?.ApiName;
                trigger["changedFields"] = new JsonArray(flow.Trigger.ChangedFields
                    .Select(id => (JsonNode?)(table?.Fields.FirstOrDefault(f => f.Id == id)?.ApiName ?? id.ToString())).ToArray());
            }
            prompt += $"\n\nThe flow as it is in the editor now — change it as asked, and keep what the person did not ask to change:\n{current.ToJsonString()}";
        }
        return prompt;
    }
}
