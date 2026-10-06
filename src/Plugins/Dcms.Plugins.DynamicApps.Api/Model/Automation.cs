using System.Text.Json.Nodes;

namespace Dcms.Plugins.DynamicApps.Api.Model;

/// <summary>One action a flow step can call.</summary>
/// <param name="Key">What a step names: <c>{Id}@{Major}</c>.</param>
/// <param name="Risk"><c>read</c>, <c>safe</c> or <c>dangerous</c>.</param>
public sealed record FlowActionInfo(string Key, string Id, int Major, string Description, string Risk, JsonObject InputSchema);

/// <param name="Status"><c>pending</c>, <c>running</c>, <c>succeeded</c>, <c>skipped</c>, <c>failed</c> or <c>terminated</c>.</param>
/// <param name="Trigger">The event name that started it.</param>
public sealed record FlowRunSummary(
    Guid Id,
    string Flow,
    int Revision,
    string Status,
    string Trigger,
    int Attempts,
    int Depth,
    int Writes,
    Guid CorrelationId,
    Guid? CausationId,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record FlowRunStepInfo(
    string StepId,
    int Attempt,
    string Action,
    string Status,
    JsonNode? Input,
    JsonNode? Output,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

/// <param name="Chain">Every run the same original action set off, oldest first: the correlation chain.</param>
public sealed record FlowRunDetail(FlowRunSummary Run, JsonNode? TriggerEvent, IReadOnlyList<FlowRunStepInfo> Steps, IReadOnlyList<FlowRunSummary> Chain);

public sealed record FlowRunPage(IReadOnlyList<FlowRunSummary> Items, int Total, int Page, int PageSize);

public sealed record StartFlowRequest(JsonObject? Input = null);

public sealed record StartFlowResult(Guid RunId);
