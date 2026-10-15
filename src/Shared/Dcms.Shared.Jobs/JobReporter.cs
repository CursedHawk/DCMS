using System.Collections.Concurrent;
using System.Text.Json;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Messaging;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Jobs;

/// <summary>
/// Reports a job's progress from a worker that cannot write the jobs schema (media-worker,
/// site-builder), over <c>jobs.state</c>; admin-api folds it into the ledger.
///
/// <para>Every call is best-effort. The work these workers do has its own record (the asset's
/// status, the build row); the ledger is the view over it, and a missed tick costs a progress
/// bar a second of accuracy, never the work.</para>
/// </summary>
public interface IJobReporter
{
    ValueTask StartedAsync(Guid jobId, Guid? tenantId, long? total = null, string? stageKey = null, CancellationToken ct = default);

    /// <summary>At most one message per job per second; the newest values win.</summary>
    ValueTask ProgressAsync(Guid jobId, Guid? tenantId, long done, long? total = null, string? stageKey = null, CancellationToken ct = default);

    ValueTask SucceededAsync(Guid jobId, Guid? tenantId, object? result = null, CancellationToken ct = default);

    ValueTask FailedAsync(Guid jobId, Guid? tenantId, string errorKey, string? detail = null, CancellationToken ct = default);
}

public sealed class BusJobReporter(IEventPublisher events, ILogger<BusJobReporter> logger) : IJobReporter
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastSent = new();

    public ValueTask StartedAsync(Guid jobId, Guid? tenantId, long? total, string? stageKey, CancellationToken ct) =>
        SendAsync(jobId, tenantId, nameof(JobStatus.Running), done: total is null ? null : 0, total, stageKey, ct: ct);

    public ValueTask ProgressAsync(Guid jobId, Guid? tenantId, long done, long? total, string? stageKey, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastSent.TryGetValue(jobId, out var last) && now - last < MinInterval)
        {
            return ValueTask.CompletedTask;
        }
        _lastSent[jobId] = now;
        return SendAsync(jobId, tenantId, status: null, done, total, stageKey, ct: ct);
    }

    public ValueTask SucceededAsync(Guid jobId, Guid? tenantId, object? result, CancellationToken ct)
    {
        _lastSent.TryRemove(jobId, out _);
        return SendAsync(jobId, tenantId, nameof(JobStatus.Succeeded), null, null, null,
            resultJson: result is null ? null : JsonSerializer.Serialize(result, JsonSerializerOptions.Web), ct: ct);
    }

    public ValueTask FailedAsync(Guid jobId, Guid? tenantId, string errorKey, string? detail, CancellationToken ct)
    {
        _lastSent.TryRemove(jobId, out _);
        return SendAsync(jobId, tenantId, nameof(JobStatus.Failed), null, null, null, errorKey, detail, ct: ct);
    }

    private async ValueTask SendAsync(
        Guid jobId, Guid? tenantId, string? status, long? done, long? total, string? stageKey,
        string? errorKey = null, string? errorDetail = null, string? resultJson = null, CancellationToken ct = default)
    {
        try
        {
            await events.PublishAsync(Subjects.JobState, new JobStateReported(
                Guid.NewGuid(), DateTimeOffset.UtcNow, jobId, tenantId, status, done, total, stageKey,
                errorKey, errorDetail, resultJson), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Could not report job {JobId}; its own record still has the outcome.", jobId);
        }
    }
}
