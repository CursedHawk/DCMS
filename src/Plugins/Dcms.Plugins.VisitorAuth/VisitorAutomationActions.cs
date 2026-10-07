using System.Text.Json;
using Dcms.Plugins.DynamicApps.Api;
using Dcms.Plugins.VisitorAuth.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.Visitors;
using Microsoft.Extensions.Logging;

namespace Dcms.Plugins.VisitorAuth;

/// <summary>
/// What visitor accounts offer Dynamic Apps flows (<c>automation.actions@1</c>): today, setting
/// the attributes a flow could otherwise only read — "mark a visitor as a customer once their
/// deal closes". The same rules as <see cref="IVisitorProfiles.SetAttributesAsync"/>: only
/// attributes shared with plugins, never a private one.
/// </summary>
public sealed class VisitorAutomationActions(IPluginContext context, VisitorsDbContext db, ILogger<VisitorProfiles> logger)
    : IAutomationActionProvider
{
    public const string SetAttributes = "visitor-auth.set-attributes";

    private static readonly JsonElement SetAttributesSchema = JsonDocument.Parse("""
        {"type":"object","required":["visitorId","attributes"],"properties":{
          "visitorId":{"type":"string","format":"uuid"},
          "attributes":{"type":"object","description":"Values by attribute key; null clears one."}}}
        """).RootElement.Clone();

    public Task<AutomationActionList> ListAsync(CancellationToken ct) => Task.FromResult(new AutomationActionList(
    [
        new AutomationActionDescriptor(SetAttributes, 1,
            "Set or clear attributes shared with plugins on a site visitor's profile (null clears one; private attributes cannot be set). Output: the profile.",
            OpRisk.Safe, SetAttributesSchema, Permission: VisitorPermissions.Manage),
    ]));

    public async Task<AutomationActionResult> ExecuteAsync(AutomationActionCall input, CancellationToken ct)
    {
        if (input.Name != SetAttributes || input.Major != 1)
        {
            throw new ContractValidationException($"Visitor accounts offer no action {input.Name}@{input.Major}.");
        }
        SetVisitorAttributes args;
        try
        {
            args = input.Input.Deserialize<SetVisitorAttributes>(JsonSerializerOptions.Web)
                   ?? throw new ContractValidationException($"{SetAttributes}: the input is empty.");
        }
        catch (JsonException e)
        {
            throw new ContractValidationException($"{SetAttributes}: the input is not valid — {e.Message}");
        }
        // Setting a value is idempotent, so a retried step needs no key of its own.
        var profile = await new VisitorProfiles(context, db, logger).SetAttributesAsync(args with { Attributes = args.Attributes ?? new Dictionary<string, JsonElement>() }, ct);
        return new AutomationActionResult(JsonSerializer.SerializeToElement(profile, JsonSerializerOptions.Web));
    }
}
