using System.Text.Json;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Messaging;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Jobs;

/// <summary>
/// <see cref="IJobQueue"/>: the row first, then the run request.
///
/// <para>The row is committed before anything is published, so the ledger is never missing a
/// job a worker is running. The publish can still be lost (NATS down, the process killed between
/// the two); the job then sits Queued until <see cref="JobMaintenanceWorker"/> sends it again.
/// That makes the table the outbox, without a second table to keep in step with it.</para>
/// </summary>
public sealed class JobQueue(
    JobLedger ledger,
    IEventPublisher events,
    AuditAmbient ambient,
    ICurrentActor actor,
    JobSource source,
    ILogger<JobQueue> logger) : IJobQueue
{
    public async Task<JobRef> EnqueueAsync(JobSpec spec, CancellationToken ct)
    {
        var job = new Job
        {
            Id = spec.Id ?? Guid.NewGuid(),
            TenantId = spec.TenantId,
            ParentId = spec.ParentId,
            Kind = spec.Kind,
            Lane = spec.Lane,
            Source = source.Name,
            TitleKey = spec.TitleKey,
            ParamsJson = spec.Params is null ? "{}" : JsonSerializer.Serialize(spec.Params, JsonSerializerOptions.Web),
            RequiredPermission = spec.RequiredPermission,
            LinkPath = spec.LinkPath,
            Routine = spec.Routine,
            LinksJson = spec.Links is { Count: > 0 } links ? JsonSerializer.Serialize(links, JsonSerializerOptions.Web) : null,
            Total = spec.Total,
            PayloadJson = spec.Payload is null ? null : JsonSerializer.Serialize(spec.Payload, JsonSerializerOptions.Web),
            // Who asked, carried to the worker so what it records names them, not the worker.
            ContextJson = AuditPropagation.CaptureJson(ambient.Current),
            Cancelable = spec.Cancelable,
            MaxAttempts = Math.Max(1, spec.MaxAttempts),
            DedupeKey = spec.DedupeKey,
            ActorUserId = actor.Kind == ActorKind.User ? actor.Id : null,
        };

        var created = await ledger.CreateAsync(job, ct);
        if (!created.Existing && JobDispatch.IsDispatched(job.Lane))
        {
            await JobDispatch.SendAsync(events, job.Id, job.TenantId, job.Kind, job.Lane, $"{job.Id}:0", logger, ct);
        }
        return created;
    }
}

/// <summary>The name a process writes into <see cref="Job.Source"/>, e.g. <c>admin-api</c>.</summary>
public sealed record JobSource(string Name);

/// <summary>Sending run requests: shared by the queue, retries and the redispatch sweep.</summary>
public static class JobDispatch
{
    /// <summary>The lanes a job worker drains. Every other lane is a queue of its own.</summary>
    public static bool IsDispatched(string lane) => lane is JobLanes.Tenant or JobLanes.Platform;

    /// <summary>
    /// Publishes the run request. A failure is logged, not thrown: the row is already committed,
    /// and the sweep re-sends anything left Queued.
    /// </summary>
    /// <param name="messageId">Nats-Msg-Id, so a retried publish inside the dupe window is one message.</param>
    public static async Task SendAsync(
        IEventPublisher events, Guid jobId, Guid? tenantId, string kind, string lane, string messageId,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            await events.PublishAsync(Subjects.JobRun(lane, kind),
                new JobRunRequested(Guid.NewGuid(), DateTimeOffset.UtcNow, jobId, tenantId, kind), ct, messageId);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not send job {JobId} ({Kind}); the redispatch sweep will.", jobId, kind);
        }
    }
}
