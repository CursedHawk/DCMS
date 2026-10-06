using System.Text.Json.Nodes;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Data;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Microsoft.AspNetCore.Http;

namespace Dcms.Plugins.DynamicApps.Endpoints;

/// <summary>
/// Records of the published tables for members, <c>/api/admin/plugins/{slug}/_records/{table}/…</c>.
/// (<c>_data</c> is the platform's data-set surface, ADR 0018.) Every route is one
/// <see cref="RecordService"/> call on the admin plane.
/// </summary>
internal static class RecordEndpoints
{
    public static void Map(IPluginEndpointBuilder endpoints)
    {
        endpoints.MapGet("/_records/{table}", (string table, int? page, int? pageSize, string? sort, string? search,
                string? select, string? expand, RecordService records, CancellationToken ct) =>
                Run(async () => Results.Ok(await records.QueryAsync(table, new RecordQuery
                {
                    Page = page ?? 1,
                    PageSize = pageSize ?? 50,
                    Search = search,
                    Select = List(select),
                    Expand = List(expand),
                    Sort = List(sort).Select(s => s.StartsWith('-')
                        ? new RecordSort(s[1..], SortDirection.Desc)
                        : new RecordSort(s)).ToList(),
                }, RecordPlane.Admin, ct))))
            .RequirePluginPermission("data-read");

        endpoints.MapPost("/_records/{table}/query", (string table, RecordQuery query, RecordService records, CancellationToken ct) =>
                Run(async () => Results.Ok(await records.QueryAsync(table, query, RecordPlane.Admin, ct))))
            .RequirePluginPermission("data-read")
            .SkipAudit("A read: the query travels in the body because a filter does not fit a URL.");

        endpoints.MapGet("/_records/{table}/{id:guid}", (string table, Guid id, string? expand, RecordService records, CancellationToken ct) =>
                Run(async () => await records.GetAsync(table, id, List(expand), RecordPlane.Admin, ct) is { } record
                    ? Results.Ok(record)
                    : NotFound(table, id)))
            .RequirePluginPermission("data-read");

        endpoints.MapPost("/_records/{table}", (string table, JsonObject values, RecordService records, CancellationToken ct) =>
                Run(async () =>
                {
                    var record = await records.CreateAsync(table, values, RecordPlane.Admin, ct);
                    return Results.Created($"_records/{table}/{record["id"]}", record);
                }))
            .RequirePluginPermission("data-write")
            .AuditAs("record.created");

        endpoints.MapPatch("/_records/{table}/{id:guid}", (string table, Guid id, JsonObject values, RecordService records, CancellationToken ct) =>
                Run(async () => await records.UpdateAsync(table, id, values, RecordPlane.Admin, ct) is { } record
                    ? Results.Ok(record)
                    : NotFound(table, id)))
            .RequirePluginPermission("data-write")
            .AuditAs("record.updated");

        endpoints.MapDelete("/_records/{table}/{id:guid}", (string table, Guid id, int? version, RecordService records, CancellationToken ct) =>
                Run(async () => await records.DeleteAsync(table, id, version, RecordPlane.Admin, ct)
                    ? Results.NoContent()
                    : NotFound(table, id)))
            .RequirePluginPermission("data-delete")
            .AuditAs("record.deleted");

        endpoints.MapPost("/_records/{table}/bulk-update", (string table, BulkUpdateRequest body, RecordService records, CancellationToken ct) =>
                Run(async () => Results.Ok(await records.BulkUpdateAsync(table, body, RecordPlane.Admin, ct))))
            .RequirePluginPermission("data-write")
            .AuditAs("record.bulk_updated");

        endpoints.MapPost("/_records/{table}/bulk-delete", (string table, BulkDeleteRequest body, RecordService records, CancellationToken ct) =>
                Run(async () => Results.Ok(await records.BulkDeleteAsync(table, body, RecordPlane.Admin, ct))))
            .RequirePluginPermission("data-delete")
            .AuditAs("record.bulk_deleted");

        endpoints.MapGet("/_records/{table}/{id:guid}/{navigation}", (string table, Guid id, string navigation, int? page, int? pageSize,
                RecordService records, CancellationToken ct) =>
                Run(async () => await records.RelatedAsync(table, id, navigation, page ?? 1, pageSize ?? 50, RecordPlane.Admin, ct) is { } related
                    ? Results.Ok(related)
                    : NotFound(table, id)))
            .RequirePluginPermission("data-read");

        endpoints.MapPost("/_records/{table}/{id:guid}/{navigation}", (string table, Guid id, string navigation, LinkRequest body,
                RecordService records, CancellationToken ct) =>
                Run(async () => await records.LinkAsync(table, id, navigation, body.TargetId, ct)
                    ? Results.NoContent()
                    : Results.NotFound(new { error = "Both records must exist." })))
            .RequirePluginPermission("data-write")
            .AuditAs("relation.linked");

        endpoints.MapDelete("/_records/{table}/{id:guid}/{navigation}/{targetId:guid}", (string table, Guid id, string navigation, Guid targetId,
                RecordService records, CancellationToken ct) =>
                Run(async () => await records.UnlinkAsync(table, id, navigation, targetId, ct)
                    ? Results.NoContent()
                    : Results.NotFound(new { error = "There is no such link." })))
            .RequirePluginPermission("data-write")
            .AuditAs("relation.unlinked");
    }

    private static IReadOnlyList<string> List(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IResult NotFound(string table, Guid id) => Results.NotFound(new { error = $"There is no {table} record {id}." });

    internal static async Task<IResult> Run(Func<Task<IResult>> handler)
    {
        try
        {
            return await handler();
        }
        catch (RecordValidationException e)
        {
            return Results.BadRequest(new { error = e.Message, fields = e.Errors });
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
