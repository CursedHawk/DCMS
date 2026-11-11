using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions.Contracts;

namespace Dcms.Plugins.Functions.Api;

/// <param name="Status">e.g. <c>failed</c>, <c>succeeded</c>; every status when omitted.</param>
public sealed record RunQuery(string? Function = null, string? Status = null, int Page = 1);

public sealed record RunRef(Guid RunId);

public sealed record KeyRef(Guid KeyId);

/// <param name="Name">The name a function reads it by (<c>secrets.get(name)</c>), as declared in its <c>uses.secrets</c>.</param>
public sealed record SecretRef(string Name);

/// <param name="Value">Write-only: stored encrypted, and never returned by any operation.</param>
public sealed record SecretWrite(string Name, string Value);

public sealed record NameList(IReadOnlyList<string> Names);

/// <summary>
/// Operating a Functions app beside its code (<see cref="IFunctionsCode"/>), for the assistant:
/// its run history, its API keys and its secrets. Secret values only go in, and a new API key is
/// made in the console only — no operation here returns a credential.
/// </summary>
[DcmsContract("functions.ops", 1, Description = "A Functions app's run history, API keys and secrets (secrets write-only).")]
public interface IFunctionsOps
{
    [Operation(OpRisk.Read, Permission = FunctionsPermissions.Logs, Expose = OpExposure.Admin | OpExposure.Ai, ReturnsExternalText = true,
        Description = "Recent runs, newest first, optionally of one function or with one status: when, how long, and whether they failed.")]
    Task<JsonNode> ListRunsAsync(RunQuery input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = FunctionsPermissions.Logs, Expose = OpExposure.Admin | OpExposure.Ai, ReturnsExternalText = true,
        Description = "One run in full: its input, result or error, logs and the calls it made.")]
    Task<JsonNode> GetRunAsync(RunRef input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = FunctionsPermissions.Keys, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "The app's API keys: name, prefix and which functions each may call. Never the keys themselves; new keys are made in the console.")]
    Task<JsonNode> ListKeysAsync(CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = FunctionsPermissions.Keys, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Revoke an API key: callers using it are refused from now on.")]
    Task RevokeKeyAsync(KeyRef input, CancellationToken ct);

    [Operation(OpRisk.Read, Permission = FunctionsPermissions.Secrets, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "The names of the app's secrets in this environment. Values are never returned.")]
    Task<NameList> ListSecretsAsync(CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = FunctionsPermissions.Secrets, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Set or replace a secret's value. Write-only: it can never be read back, so take the value from the operator, never invent one.")]
    Task SetSecretAsync(SecretWrite input, CancellationToken ct);

    [Operation(OpRisk.Dangerous, Permission = FunctionsPermissions.Secrets, Expose = OpExposure.Admin | OpExposure.Ai,
        Description = "Delete a secret: functions reading it get nothing from now on.")]
    Task DeleteSecretAsync(SecretRef input, CancellationToken ct);
}
