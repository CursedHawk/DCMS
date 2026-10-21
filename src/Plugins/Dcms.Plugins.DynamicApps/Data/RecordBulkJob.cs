using System.Text.Json;
using Dcms.Plugins.DynamicApps.Api.Model;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;

namespace Dcms.Plugins.DynamicApps.Data;

/// <param name="Op"><c>import</c>, <c>bulk-update</c> or <c>bulk-delete</c>.</param>
/// <param name="BlobPrefix">Where the request body waits, in <see cref="Parts"/> pieces.</param>
/// <param name="Actor">Who queued it; the records are theirs, not the worker's.</param>
public sealed record RecordBulkPayload(string Op, string Table, string BlobPrefix, int Parts, bool DryRun, string? Actor);

/// <summary>
/// An import, bulk update or bulk delete larger than a request may carry (ADR 0024), run by
/// jobs-worker as a tracked job: the bar follows the rows, it can be canceled, and its result --
/// an import's counts and errors -- lands on the job. Still all or nothing: the same
/// <see cref="RecordService"/> call as the request path, in one transaction, only with a higher
/// ceiling and a progress callback.
/// </summary>
public sealed class RecordBulkJob(RecordService records) : IPluginJobHandler
{
    public const string Name = "records-bulk";

    /// <summary>The ceilings for a background job, against the request path's 1,000 / 500.</summary>
    public const int MaxRows = 100_000;

    public const int MaxCsvChars = 20 * 1024 * 1024;

    /// <summary>Under dcms.blobs' 10 MiB per object.</summary>
    private const int PartBytes = 8 * 1024 * 1024;

    /// <summary>Stores the request and queues the job; the caller answers 202 with the ticket.</summary>
    public static async Task<JobTicket> EnqueueAsync(
        IPluginContext context, string op, string table, object request, bool dryRun, int count, string permission,
        CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, request.GetType(), JsonSerializerOptions.Web);
        var blobs = context.Contracts.Get<IPluginBlobs>();
        var prefix = $"bulk/{Guid.NewGuid():N}";
        var parts = (bytes.Length + PartBytes - 1) / PartBytes;
        for (var i = 0; i < parts; i++)
        {
            var slice = bytes.AsSpan(i * PartBytes, Math.Min(PartBytes, bytes.Length - i * PartBytes)).ToArray();
            await blobs.PutAsync(new BlobPut($"{prefix}/{i}", "application/json", slice), ct);
        }
        var payload = new RecordBulkPayload(op, table, prefix, parts, dryRun, context.Actor.Id?.ToString() ?? context.Actor.Key);
        return await context.Contracts.Get<IPluginTrackedJobs>().EnqueueAsync(new TrackedJobEnqueue(
            Name,
            JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web),
            TitleKey: $"jobs.kinds.records.{op.Replace("-", "")}.title",
            TitleParams: new Dictionary<string, string> { ["table"] = table, ["count"] = count.ToString() },
            RequiredPermission: permission,
            Links: [new TrackedJobLink("result", $"/apps/{context.Instance?.Slug}/records?table={Uri.EscapeDataString(table)}", table)]), ct);
    }

    public async Task RunAsync(JsonElement? payload, IPluginContext context, CancellationToken ct)
    {
        var job = payload?.Deserialize<RecordBulkPayload>(JsonSerializerOptions.Web)
                  ?? throw new ContractValidationException("A bulk job needs its payload.");
        var blobs = context.Contracts.Get<IPluginBlobs>();
        var run = PluginJobRun.Current;
        try
        {
            using var body = new MemoryStream();
            for (var i = 0; i < job.Parts; i++)
            {
                var part = await blobs.GetAsync(new BlobKey($"{job.BlobPrefix}/{i}"), ct)
                           ?? throw new ContractValidationException("The bulk request is no longer stored; queue it again.");
                body.Write(part.Content);
            }
            var json = body.ToArray();

            records.ActingAs = job.Actor;
            RecordService.Progress? progress = run is null
                ? null
                : (done, total, stage) => run.ReportAsync(done, total, stage);

            object result = job.Op switch
            {
                "import" => await records.ImportAsync(job.Table, Read<ImportRequest>(json), job.DryRun, ct,
                    MaxRows, MaxCsvChars, progress),
                "bulk-update" => await records.BulkUpdateAsync(job.Table, Read<BulkUpdateRequest>(json), RecordPlane.Admin, ct,
                    MaxRows, progress),
                "bulk-delete" => await records.BulkDeleteAsync(job.Table, Read<BulkDeleteRequest>(json), RecordPlane.Admin, ct,
                    MaxRows, progress),
                _ => throw new ContractValidationException($"Unknown bulk operation '{job.Op}'."),
            };
            if (run is not null)
            {
                run.Result = result;
            }
        }
        finally
        {
            for (var i = 0; i < job.Parts; i++)
            {
                await blobs.DeleteAsync(new BlobKey($"{job.BlobPrefix}/{i}"), CancellationToken.None);
            }
        }
    }

    private static T Read<T>(byte[] json) =>
        JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web)
        ?? throw new ContractValidationException("The bulk request could not be read.");
}
