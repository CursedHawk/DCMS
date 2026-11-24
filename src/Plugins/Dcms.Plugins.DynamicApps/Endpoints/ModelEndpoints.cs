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
            .RequirePluginPermission("model-read")
            .Cli("model", "The app's data model as people see it now: tables, fields, relations.", OpRisk.Read);

        endpoints.MapGet("/_model/draft", (ConfigurationService service, CancellationToken ct) =>
                Run(async () => await service.GetDraftAsync(ct) is { } draft ? Results.Ok(draft) : NotFound("There is no open draft.")))
            .RequirePluginPermission("model-read")
            .Cli("model-draft", "The app's draft model, with the changes not yet published.", OpRisk.Read);

        endpoints.MapGet("/_model/published", (ConfigurationService service, CancellationToken ct) =>
                Run(async () => await service.GetPublishedAsync(ct) is { } live ? Results.Ok(live) : NotFound("Nothing has been published yet.")))
            .RequirePluginPermission("model-read")
            .Cli("model-published", "The app's published model.", OpRisk.Read);

        /*
         * The published model as somebody browsing records needs it — tables, fields, choices,
         * relationships and views — for a member who may read the app's data but not its
         * configuration. The Records screen used /_model/published, which is model-read, so a
         * data-read member reached a screen that could not draw. Flows are left out: they are
         * configuration (an automation can name a webhook target), not the shape of the data.
         */
        endpoints.MapGet("/_model/records", (ConfigurationService service, CancellationToken ct) =>
                Run(async () => await service.GetPublishedAsync(ct) is { } live
                    ? Results.Ok(live with { Config = live.Config with { Flows = [] } })
                    : NotFound("Nothing has been published yet.")))
            .RequirePluginPermission("data-read")
            .Cli("model-records", "The published model as the records screens use it.", OpRisk.Read);

        endpoints.MapGet("/_model/public", async (RuntimeModelProvider models, CancellationToken ct) =>
                await models.GetAsync(ct) is { } model ? Results.Ok(PublicApi.Model(model)) : NotFound("Nothing has been published yet."))
            .RequirePluginPermission("model-read")
            .Cli("model-public", "What of the model sites may read.", OpRisk.Read);

        endpoints.MapPost("/_model/draft", (CreateDraftRequest? body, ConfigurationService service, CancellationToken ct) =>
                Run(async () => Results.Ok(await service.CreateDraftAsync(body?.Description, AppChangeSource.Human, ct))))
            .RequirePluginPermission("model-write")
            .AuditAs("revision.created")
            .Cli("start-draft", "Starts a draft of the model from the published one.", OpRisk.Safe);

        endpoints.MapPost("/_model/draft/changes", (ApplyChangesRequest body, ConfigurationService service, CancellationToken ct) =>
                Run(async () => Results.Ok(await service.ApplyAsync(body, AppChangeSource.Human, ct))))
            .RequirePluginPermission("model-write")
            .AuditAs("change.applied")
            .Cli("change-draft", "Applies changes to the draft model: tables, fields, relations.", OpRisk.Safe);

        endpoints.MapPost("/_model/draft/validate", (ConfigurationService service, CancellationToken ct) =>
                Run(async () => Results.Ok(await service.ValidateAsync(ct))))
            .RequirePluginPermission("model-write")
            .AuditAs("revision.validated")
            .Cli("validate-draft", "Checks the draft model against the data already there.", OpRisk.Read);

        endpoints.MapGet("/_model/draft/preview", (ConfigurationService service, CancellationToken ct) =>
                Run(async () => Results.Ok(await service.PreviewAsync(ct))))
            .RequirePluginPermission("model-read")
            .Cli("preview-draft", "What publishing the draft would change.", OpRisk.Read);

        endpoints.MapPost("/_model/draft/discard", (ExpectedHashRequest body, ConfigurationService service, CancellationToken ct) =>
                Run(async () =>
                {
                    await service.DiscardAsync(body.ExpectedHash, ct);
                    return Results.NoContent();
                }))
            .RequirePluginPermission("model-write")
            .AuditAs("revision.discarded")
            .Cli("discard-draft", "Throws the draft model away.", OpRisk.Dangerous);

        endpoints.MapPost("/_model/draft/publish", (ExpectedHashRequest body, ConfigurationService service, CancellationToken ct) =>
                Run(async () => Published(await service.PublishAsync(body.ExpectedHash, ct))))
            .RequirePluginPermission("publish")
            .AuditAs("revision.published")
            .Cli("publish-draft", "Publishes the draft model: tables and fields change for everyone.", OpRisk.Dangerous);

        endpoints.MapPost("/_model/rollback", (RollbackRequest body, ConfigurationService service, CancellationToken ct) =>
                Run(async () => Published(await service.RollbackAsync(body, AppChangeSource.Human, ct))))
            .RequirePluginPermission("publish")
            .AuditAs("revision.rolled_back")
            .Cli("rollback-model", "Makes an earlier revision of the model the live one.", OpRisk.Dangerous);

        endpoints.MapGet("/_model/revisions", (int? page, int? pageSize, ConfigurationService service, CancellationToken ct) =>
                Run(async () => Results.Ok(await service.ListRevisionsAsync(page ?? 1, pageSize ?? 20, ct))))
            .RequirePluginPermission("model-read")
            .Cli("model-revisions", "The model's published revisions.", OpRisk.Read);

        endpoints.MapGet("/_model/revisions/{number:int}", (int number, ConfigurationService service, CancellationToken ct) =>
                Run(async () => await service.GetRevisionAsync(number, ct) is { } doc ? Results.Ok(doc) : NotFound($"There is no revision {number}.")))
            .RequirePluginPermission("model-read")
            .Cli("model-revision", "One revision of the model.", OpRisk.Read);

        endpoints.MapGet("/_model/revisions/{number:int}/changes", (int number, ConfigurationService service, CancellationToken ct) =>
                Run(async () => await service.GetChangesAsync(number, ct) is { } changes ? Results.Ok(changes) : NotFound($"There is no revision {number}.")))
            .RequirePluginPermission("model-read")
            .Cli("model-revision-changes", "What one revision changed.", OpRisk.Read);

        endpoints.MapGet("/_model/diff", (int from, int to, ConfigurationService service, CancellationToken ct) =>
                Run(async () => await service.DiffAsync(from, to, ct) is { } diff ? Results.Ok(diff) : NotFound("Both revisions must exist.")))
            .RequirePluginPermission("model-read")
            .Cli("model-diff", "The difference between two revisions of the model.", OpRisk.Read);
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
