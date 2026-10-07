using System.Text.Json;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.DynamicApps.Api;

/// <summary>
/// One action a plugin offers to Dynamic Apps flows. A flow names it <c>{Name}@{Major}</c>, so
/// its meaning must never change within a major version: a change of meaning is a new major,
/// offered alongside the old one.
/// </summary>
/// <param name="Name">Prefixed with the providing plugin's id, e.g. <c>visitor-auth.set-attributes</c>; lowercase, dots and dashes.</param>
/// <param name="Risk">What the action does, as the designer and the assistant show it.</param>
/// <param name="InputSchema">JSON Schema of the input the flow step passes.</param>
/// <param name="Permission">
/// What a member must hold to publish a flow that uses the action: the permission your plugin
/// asks of an admin doing the same thing by hand. Without it, a flow would let anyone who may
/// publish an app do what your plugin would refuse them.
/// </param>
public sealed record AutomationActionDescriptor(
    string Name, int Major, string Description, OpRisk Risk, JsonElement InputSchema, string? Permission = null);

public sealed record AutomationActionList(IReadOnlyList<AutomationActionDescriptor> Actions);

/// <param name="Input">The step's input, its templates already rendered.</param>
/// <param name="IdempotencyKey">The same for every retry of the step: what a side effect should deduplicate on.</param>
/// <param name="AppInstanceId">The Dynamic Apps instance whose flow is running.</param>
public sealed record AutomationActionCall(
    string Name, int Major, JsonElement Input, string IdempotencyKey, Guid AppInstanceId, Guid RunId);

/// <param name="Output">What later steps read as <c>steps.{id}</c>.</param>
public sealed record AutomationActionResult(JsonElement? Output);

/// <summary>
/// Actions another plugin offers to Dynamic Apps flows (ADR 0021). Dynamic Apps calls every
/// enabled provider through the contract runtime, so a provider's work is audited and gated like
/// any plugin-to-plugin call. A failure the same input would repeat should throw
/// <see cref="ContractValidationException"/>: the step then fails without being retried.
/// </summary>
[DcmsContract("automation.actions", 1, Description = "Actions a plugin offers to Dynamic Apps flows.")]
public interface IAutomationActionProvider
{
    [Operation(OpRisk.Read, Description = "The actions this plugin offers.")]
    Task<AutomationActionList> ListAsync(CancellationToken ct);

    [Operation(OpRisk.Safe, Description = "Run one of the offered actions for a flow step.")]
    Task<AutomationActionResult> ExecuteAsync(AutomationActionCall input, CancellationToken ct);
}
