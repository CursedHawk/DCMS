using Dcms.Plugins.Functions.Build;
using Dcms.Plugins.Functions.Deploy;
using Dcms.Plugins.Functions.Http;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using System.Text.Json.Nodes;
using Dcms.Shared.Security.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Dcms.Plugins.Functions.Endpoints;

/// <summary>
/// Members' routes, <c>/api/admin/plugins/{slug}/_functions/…</c>: the instance's repository
/// (which the host's IDE edits at <c>/api/admin/repos/{id}</c>), its builds and deploying them.
/// </summary>
internal static class AdminEndpoints
{
    public static void Map(IPluginEndpointBuilder endpoints)
    {
        // The repository, created with the starter the first time anyone opens the code.
        endpoints.MapGet("/_functions/repo", (DeploymentService deployments, CancellationToken ct) =>
                Run(async () => Results.Ok(await deployments.RepoAsync(ct))))
            .RequirePluginPermission("read");

        // @dcms/functions as the editor's type checker sees it: what a build checks against now.
        endpoints.MapGet("/_functions/sdk.d.ts", async (HttpContext http, SdkTypings sdk, IAuthorizationService authz, CancellationToken ct) =>
            {
                // Only what the reader may see of the tables and contracts it describes.
                var typings = await sdk.GenerateAsync(ct, async permission =>
                    (await authz.AuthorizeAsync(http.User, null, new PermissionRequirement(permission))).Succeeded);
                var etag = SdkTypings.ETag(typings);
                http.Response.Headers.ETag = etag;
                http.Response.Headers.CacheControl = "private, no-cache";
                return http.Request.Headers.IfNoneMatch == etag
                    ? Results.StatusCode(StatusCodes.Status304NotModified)
                    : Results.Text(typings, "application/typescript; charset=utf-8");
            })
            .RequirePluginPermission("read");

        // The same, as JSON for the console's client, which reads every answer as JSON.
        endpoints.MapGet("/_functions/sdk", async (HttpContext http, SdkTypings sdk, IAuthorizationService authz, CancellationToken ct) =>
            {
                var typings = await sdk.GenerateAsync(ct, async permission =>
                    (await authz.AuthorizeAsync(http.User, null, new PermissionRequirement(permission))).Succeeded);
                return Results.Ok(new { typings, etag = SdkTypings.ETag(typings) });
            })
            .RequirePluginPermission("read");

        endpoints.MapGet("/_functions/deployments", (DeploymentService deployments, CancellationToken ct) =>
                Run(async () => Results.Ok(await deployments.ListAsync(ct))))
            .RequirePluginPermission("read");

        endpoints.MapGet("/_functions/deployments/{id:guid}", (Guid id, DeploymentService deployments, CancellationToken ct) =>
                Run(async () => await deployments.GetAsync(id, ct) is { } deployment
                    ? Results.Ok(deployment)
                    : Results.NotFound(new { error = $"There is no deployment {id}." })))
            .RequirePluginPermission("read");

        // Deploy a build, or roll back to an earlier one: the caller must hold what it uses.
        endpoints.MapPost("/_functions/deployments/{id:guid}/activate", (Guid id, DeploymentService deployments, CancellationToken ct) =>
                Run(async () => await deployments.ActivateAsync(id, ct) is { } live
                    ? Results.Ok(live)
                    : Results.NotFound(new { error = $"There is no deployment {id}." })))
            .RequirePluginPermission("deploy")
            .AuditAs("deployment.activated");

        endpoints.MapPost("/_functions/build", (DeploymentService deployments, CancellationToken ct) =>
                Run(async () => Results.Accepted(value: await deployments.RebuildAsync(ct))))
            .RequirePluginPermission("deploy")
            .AuditAs("build.requested");

        // The run history: inputs, results and logs are the tenant's data, so the logs permission.
        endpoints.MapGet("/_functions/runs", (string? function, string? status, int? page, DeploymentService deployments, CancellationToken ct) =>
                Run(async () => Results.Ok(await deployments.RunsAsync(function, status, page ?? 1, ct))))
            .RequirePluginPermission("logs");

        endpoints.MapGet("/_functions/runs/{id:guid}", (Guid id, DeploymentService deployments, CancellationToken ct) =>
                Run(async () => await deployments.RunAsync(id, ct) is { } run
                    ? Results.Ok(run)
                    : Results.NotFound(new { error = $"There is no run {id}." })))
            .RequirePluginPermission("logs");

        // API keys: the secret is in the answer to the create, once, and stored only as its hash.
        endpoints.MapGet("/_functions/keys", (ApiKeyService keys, CancellationToken ct) =>
                Run(async () => Results.Ok(await keys.ListAsync(ct))))
            .RequirePluginPermission("keys");

        endpoints.MapPost("/_functions/keys", (CreateApiKey body, ApiKeyService keys, CancellationToken ct) =>
                Run(async () => Results.Ok(await keys.CreateAsync(body, ct))))
            .RequirePluginPermission("keys")
            .AuditAs("key.created");

        endpoints.MapDelete("/_functions/keys/{id:guid}", (Guid id, ApiKeyService keys, CancellationToken ct) =>
                Run(async () => await keys.RevokeAsync(id, ct)
                    ? Results.NoContent()
                    : Results.NotFound(new { error = $"There is no key {id}." })))
            .RequirePluginPermission("keys")
            .AuditAs("key.revoked");

        // Secrets, per instance and so per environment: names listed, values only ever written.
        endpoints.MapGet("/_functions/secrets", (IPluginContext context, CancellationToken ct) =>
                Run(async () => Results.Ok((await context.Contracts.Get<IPluginSecrets>()
                    .ListAsync(new SecretNames(context.Instance!.InstanceId), ct)).Names)))
            .RequirePluginPermission("secrets");

        endpoints.MapPut("/_functions/secrets/{name}", (string name, SecretBody body, IPluginContext context, CancellationToken ct) =>
                Run(async () =>
                {
                    await context.Contracts.Get<IPluginSecrets>().SetAsync(new SetSecret(name, body.Value, context.Instance!.InstanceId), ct);
                    return Results.NoContent();
                }))
            .RequirePluginPermission("secrets")
            .AuditAs("secret.set");

        endpoints.MapDelete("/_functions/secrets/{name}", (string name, IPluginContext context, CancellationToken ct) =>
                Run(async () => (await context.Contracts.Get<IPluginSecrets>().DeleteAsync(new SecretName(name, context.Instance!.InstanceId), ct)).Found
                    ? Results.NoContent()
                    : Results.NotFound(new { error = $"There is no secret {name}." })))
            .RequirePluginPermission("secrets")
            .AuditAs("secret.deleted");

        // Runs a function now: the live deployment, or a built one tried before deploying it,
        // which needs what deploying it would. Either way it acts, so only deployers may.
        endpoints.MapPost("/_functions/run", (RunRequest body, DeploymentService deployments, CancellationToken ct) =>
                Run(async () =>
                {
                    var outcome = await deployments.RunAsync(body.Function, body.Input, body.Deployment, ct);
                    return Results.Ok(new { outcome.Status, outcome.Result, outcome.Error, outcome.Logs, outcome.Calls, outcome.DurationMs });
                }))
            .RequirePluginPermission("deploy")
            .AuditAs("function.ran");
    }

    public sealed record SecretBody(string Value);

    public sealed record RunRequest(string Function, JsonNode? Input = null, Guid? Deployment = null);

    private static async Task<IResult> Run(Func<Task<IResult>> handler)
    {
        try
        {
            return await handler();
        }
        catch (DeployRefusedException e)
        {
            return Results.Json(new { error = e.Message, missing = e.Missing }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ContractConflictException e)
        {
            return Results.Conflict(new { error = e.Message });
        }
        catch (ContractValidationException e)
        {
            return Results.BadRequest(new { error = e.Message });
        }
    }
}
