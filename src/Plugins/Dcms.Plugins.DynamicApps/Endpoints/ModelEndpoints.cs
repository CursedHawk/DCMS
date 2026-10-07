using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.Plugins.DynamicApps.Metadata;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.Shared.Data.DynamicApps;
using Microsoft.AspNetCore.Http;

namespace Dcms.Plugins.DynamicApps.Endpoints;

/// <summary>
/// The control plane over HTTP, <c>/api/admin/plugins/{slug}/_model/…</c>: the configuration,
/// its draft, its revisions, publishing and rollback. Thin: every route is one
/// <see cref="ConfigurationService"/> call, which is what the assistant's contract calls too.
/// </summary>
internal static class ModelEndpoints
{
    public static void Map(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapGet("/_model", (ConfigurationService service, CancellationToken ct) =>
                Run(async () => Results.Ok(await service.GetStateAsync(ct))))
            .RequirePluginPermission("model-read");

        endpoints.MapGet("/_model/draft", (ConfigurationService service, CancellationToken ct) =>
                Run(async () => await service.GetDraftAsync(ct) is { } draft ? Results.Ok(draft) : NotFound("There is no open draft.")))
            .RequirePluginPermission("model-read");

        endpoints.MapGet("/_model/published", (ConfigurationService service, CancellationToken ct) =>
                Run(async () => await service.GetPublishedAsync(ct) is { } live ? Results.Ok(live) : NotFound("Nothing has been published yet.")))
            .RequirePluginPermission("model-read");

        endpoints.MapGet("/_model/public", async (RuntimeModelProvider models, CancellationToken ct) =>
                await models.GetAsync(ct) is { } model ? Results.Ok(PublicApi.Model(model)) : NotFound("Nothing has been published yet."))
            .RequirePluginPermission("model-read");

        endpoints.MapPost("/_model/draft", (CreateDraftRequest? body, ConfigurationService service, CancellationToken ct) =>
                Run(async () => Results.Ok(await service.CreateDraftAsync(body?.Description, AppChangeSource.Human, ct))))
            .RequirePluginPermission("model-write")
            .AuditAs("revision.created");

        endpoints.MapPost("/_model/draft/changes", (ApplyChangesRequest body, ConfigurationService service, CancellationToken ct) =>
                Run(async () => Results.Ok(await service.ApplyAsync(body, AppChangeSource.Human, ct))))
            .RequirePluginPermission("model-write")
            .AuditAs("change.applied");

        endpoints.MapPost("/_model/draft/validate", (ConfigurationService service, CancellationToken ct) =>
                Run(async () => Results.Ok(await service.ValidateAsync(ct))))
            .RequirePluginPermission("model-write")
            .AuditAs("revision.validated");

        endpoints.MapGet("/_model/draft/preview", (ConfigurationService service, CancellationToken ct) =>
                Run(async () => Results.Ok(await service.PreviewAsync(ct))))
            .RequirePluginPermission("model-read");

        endpoints.MapPost("/_model/draft/discard", (ExpectedHashRequest body, ConfigurationService service, CancellationToken ct) =>
                Run(async () =>
                {
                    await service.DiscardAsync(body.ExpectedHash, ct);
                    return Results.NoContent();
                }))
            .RequirePluginPermission("model-write")
            .AuditAs("revision.discarded");

        endpoints.MapPost("/_model/draft/publish", (ExpectedHashRequest body, ConfigurationService service, CancellationToken ct) =>
                Run(async () => Published(await service.PublishAsync(body.ExpectedHash, ct))))
            .RequirePluginPermission("publish")
            .AuditAs("revision.published");

        endpoints.MapPost("/_model/rollback", (RollbackRequest body, ConfigurationService service, CancellationToken ct) =>
                Run(async () => Published(await service.RollbackAsync(body, AppChangeSource.Human, ct))))
            .RequirePluginPermission("publish")
            .AuditAs("revision.rolled_back");

        endpoints.MapGet("/_model/revisions", (int? page, int? pageSize, ConfigurationService service, CancellationToken ct) =>
                Run(async () => Results.Ok(await service.ListRevisionsAsync(page ?? 1, pageSize ?? 20, ct))))
            .RequirePluginPermission("model-read");

        endpoints.MapGet("/_model/revisions/{number:int}", (int number, ConfigurationService service, CancellationToken ct) =>
                Run(async () => await service.GetRevisionAsync(number, ct) is { } doc ? Results.Ok(doc) : NotFound($"There is no revision {number}.")))
            .RequirePluginPermission("model-read");

        endpoints.MapGet("/_model/revisions/{number:int}/changes", (int number, ConfigurationService service, CancellationToken ct) =>
                Run(async () => await service.GetChangesAsync(number, ct) is { } changes ? Results.Ok(changes) : NotFound($"There is no revision {number}.")))
            .RequirePluginPermission("model-read");

        endpoints.MapGet("/_model/diff", (int from, int to, ConfigurationService service, CancellationToken ct) =>
                Run(async () => await service.DiffAsync(from, to, ct) is { } diff ? Results.Ok(diff) : NotFound("Both revisions must exist.")))
            .RequirePluginPermission("model-read");
    }

    /// <summary>A refused publish is not an error of the request: the body says why, as 422.</summary>
    private static IResult Published(PublishResult result) =>
        result.Published ? Results.Ok(result) : Results.UnprocessableEntity(result);

    private static async Task<IResult> Run(Func<Task<IResult>> handler)
    {
        try
        {
            return await handler();
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

    private static IResult NotFound(string message) => Results.NotFound(new { error = message });
}
