using System.Text.Json.Nodes;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.Plugins.DynamicApps.Data;
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

        // Up to the request ceilings these answer at once, as they always have. Past them -- up to
        // RecordBulkJob's -- the work is queued as a tracked job (ADR 0024) and the answer is 202
        // with its id: the console follows its progress, and the result arrives on the job.
        endpoints.MapPost("/_records/{table}/bulk-update", (string table, BulkUpdateRequest body, RecordService records,
                IPluginContext context, CancellationToken ct) =>
                Run(async () => body.Ids.Count > RecordService.MaxBulk
                    ? Queued(await RecordBulkJob.EnqueueAsync(context, "bulk-update", table, Capped(body.Ids, body), false,
                        body.Ids.Count, "data-write", ct))
                    : Results.Ok(await records.BulkUpdateAsync(table, body, RecordPlane.Admin, ct))))
            .RequirePluginPermission("data-write")
            .AuditAs("record.bulk_updated");

        endpoints.MapPost("/_records/{table}/bulk-delete", (string table, BulkDeleteRequest body, RecordService records,
                IPluginContext context, CancellationToken ct) =>
                Run(async () => body.Ids.Count > RecordService.MaxBulk
                    ? Queued(await RecordBulkJob.EnqueueAsync(context, "bulk-delete", table, Capped(body.Ids, body), false,
                        body.Ids.Count, "data-delete", ct))
                    : Results.Ok(await records.BulkDeleteAsync(table, body, RecordPlane.Admin, ct))))
            .RequirePluginPermission("data-delete")
            .AuditAs("record.bulk_deleted");

        endpoints.MapPost("/_records/{table}/import", (string table, bool? dryRun, ImportRequest body, RecordService records,
                IPluginContext context, CancellationToken ct) =>
                Run(async () => ImportRows(body) is var rows && (rows > RecordService.MaxImport || body.Csv?.Length > CsvRows.MaxChars)
                    ? Queued(await RecordBulkJob.EnqueueAsync(context, "import", table, CappedImport(body, rows), dryRun ?? false,
                        rows, "data-import", ct))
                    : Results.Ok(await records.ImportAsync(table, body, dryRun ?? false, ct))))
            .RequirePluginPermission("data-import")
            .AuditAs("record.imported");

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

    private static IResult Queued(JobTicket ticket) =>
        Results.Accepted($"/api/admin/jobs/{ticket.JobId}", new { jobId = ticket.JobId });

    /// <summary>Refused before anything is stored: a job's ceiling, checked where the caller can be told.</summary>
    private static T Capped<T>(IReadOnlyList<Guid> ids, T body) =>
        ids.Count <= RecordBulkJob.MaxRows
            ? body
            : throw new ContractValidationException($"A bulk operation takes at most {RecordBulkJob.MaxRows:N0} ids.");

    private static ImportRequest CappedImport(ImportRequest body, int rows) =>
        rows <= RecordBulkJob.MaxRows && body.Csv?.Length is null or <= RecordBulkJob.MaxCsvChars
            ? body
            : throw new ContractValidationException(
                $"An import takes at most {RecordBulkJob.MaxRows:N0} rows and {RecordBulkJob.MaxCsvChars / 1024 / 1024} MB of CSV.");

    /// <summary>
    /// How many rows the request carries -- for a CSV, its lines less the header. A quoted line
    /// break overcounts, which can only send a borderline import to the background; never the
    /// other way.
    /// </summary>
    private static int ImportRows(ImportRequest body) =>
        body.Rows?.Count ?? (body.Csv is { } csv ? Math.Max(0, csv.AsSpan().Count('\n') + (csv.EndsWith('\n') ? 0 : 1) - 1) : 0);

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
        catch (PublicAccessException e)
        {
            return Results.Json(new { error = e.Message }, statusCode: e.Status);
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
