using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Automation;
using Dcms.PluginSdk.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Dcms.Plugins.DynamicApps.Endpoints;

/// <summary>
/// Automations for members, <c>/api/admin/plugins/{slug}/_automation/…</c>: the action catalog,
/// run history with each run's steps and correlation chain, retry, and starting a manual flow.
/// Flows themselves are configuration, changed through <c>_model</c> change sets.
/// </summary>
internal static class AutomationEndpoints
{
    public static void Map(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapGet("/_automation/actions", async (FlowRunService runs, CancellationToken ct) => Results.Ok(await runs.ActionsAsync(ct)))
            .RequirePluginPermission("model-read");

        // The expression language as the flow editor offers it. Static: what the server parses.
        endpoints.MapGet("/_automation/language", () => Results.Ok(new
            {
                functions = Expressions.FunctionDocs.Select(f => new { name = f.Key, f.Value.Signature, f.Value.Description, f.Value.MinArgs, f.Value.MaxArgs, f.Value.Returns }),
                variables = Metadata.ConfigValidator.FlowVariables,
                maxSourceChars = Expressions.MaxSourceChars,
            }))
            .RequirePluginPermission("model-read");

        // An expression's problems while it is typed: parsed, never evaluated, so it reads no data.
        endpoints.MapPost("/_automation/check", (CheckExpressionRequest body) =>
                Results.Ok(new { problems = Expressions.Check(body.Source ?? "", Metadata.ConfigValidator.FlowVariables) }))
            .RequirePluginPermission("model-read");

        // Runs carry the records that triggered them, so they are read with the data.
        endpoints.MapGet("/_automation/runs", (string? flow, string? status, int? page, int? pageSize, FlowRunService runs, CancellationToken ct) =>
                RecordEndpoints.Run(async () => Results.Ok(await runs.ListAsync(flow, status, page ?? 1, pageSize ?? 50, ct))))
            .RequirePluginPermission("data-read");

        endpoints.MapGet("/_automation/runs/{id:guid}", (Guid id, FlowRunService runs, CancellationToken ct) =>
                RecordEndpoints.Run(async () => await runs.GetAsync(id, ct) is { } run
                    ? Results.Ok(run)
                    : Results.NotFound(new { error = $"There is no run {id}." })))
            .RequirePluginPermission("data-read");

        endpoints.MapPost("/_automation/runs/{id:guid}/retry", (Guid id, FlowRunService runs, CancellationToken ct) =>
                RecordEndpoints.Run(async () => await runs.RetryAsync(id, ct)
                    ? Results.Accepted()
                    : Results.NotFound(new { error = $"There is no run {id}." })))
            .RequirePluginPermission("flows-run")
            .AuditAs("flow.retried");

        endpoints.MapPost("/_automation/flows/{flow}/run", (string flow, StartFlowRequest? body, FlowRunService runs, CancellationToken ct) =>
                RecordEndpoints.Run(async () => Results.Accepted(value: new StartFlowResult(await runs.StartAsync(flow, body?.Input ?? [], ct)))))
            .RequirePluginPermission("flows-run")
            .AuditAs("flow.started");
    }
}

internal sealed record CheckExpressionRequest(string? Source);
