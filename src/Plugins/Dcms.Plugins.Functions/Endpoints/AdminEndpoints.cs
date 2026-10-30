using Dcms.Plugins.Functions.Deploy;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
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
    }

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
