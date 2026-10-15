using System.Diagnostics;
using System.Text.Json;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Propagation;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Jobs;
using Dcms.Shared.Kernel.Abstractions;
using Dcms.Shared.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.Shared.Jobs;

/// <summary>
/// Drains one lane of <c>JOBS</c> (ADR 0024): claims each job on the ledger, runs its handler in
/// the job's tenant and actor, keeps the lease and the JetStream ack alive while it runs, and
/// records how it ended.
///
/// <para><b>The ledger decides, not the message.</b> A run request is only a pointer. The claim
/// is a conditional update, so a duplicate request, a redelivery after the work finished, or a
/// job canceled while queued all find nothing to claim and are acked away. That is what makes
/// "processed at most once at a time, and never again once done" a property of the row rather
/// than of how carefully every publisher behaved.</para>
///
/// <para><b>Retries belong to the ledger too.</b> The consumer redelivers without limit; whether
/// another attempt happens is <see cref="JobLedger.FailAsync"/>'s answer, from the row's own
/// attempt count. JetStream's <c>MaxDeliver</c> cannot tell a failure from a host shutdown.</para>
/// </summary>
public sealed class JobConsumerHost(
    string lane,
    int concurrency,
    IServiceProvider services,
    INatsJSContext jetStream,
    JobHandlerRegistry registry,
    DcmsMetrics metrics,
    ILogger<JobConsumerHost> logger) : BackgroundService
{
    private static readonly TimeSpan AckWait = TimeSpan.FromMinutes(2);

    /// <summary>Lease renewal, JetStream progress ack and cancel check, all on one tick.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);

    private string DurableName => $"jobs-{lane}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(Streams.Jobs, new ConsumerConfig(DurableName)
                {
                    FilterSubject = Subjects.JobRunLane(lane),
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    AckWait = AckWait,
                    MaxAckPending = concurrency,
                    MaxDeliver = -1,
                }, stoppingToken);

                await Parallel.ForEachAsync(
                    consumer.ConsumeAsync<JobRunRequested>(cancellationToken: stoppingToken),
                    new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = stoppingToken },
                    async (msg, _) => await HandleAsync(msg, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Durable} unavailable; retrying in 5s.", DurableName);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleAsync(INatsJSMsg<JobRunRequested> msg, CancellationToken stopping)
    {
        if (msg.Data is not { } request)
        {
            logger.LogError("{Durable} could not read a run request on {Subject}; dropping it.", DurableName, msg.Subject);
            await msg.AckTerminateAsync(cancellationToken: stopping);
            return;
        }

        // The control scope: claim, heartbeat and outcome. The handler gets a scope of its own,
        // so nothing it does to its DbContexts can collide with these writes.
        using var control = services.CreateScope();
        var ledger = control.ServiceProvider.GetRequiredService<JobLedger>();

        var job = await ledger.ClaimAsync(request.JobId, request.TenantId, stopping);
        if (job is null)
        {
            logger.LogDebug("Job {JobId} is not claimable (running elsewhere, finished or canceled); acking.", request.JobId);
            await msg.AckAsync(cancellationToken: stopping);
            return;
        }

        var registration = registry.Find(job.Kind);
        if (registration is null)
        {
            // A kind this build does not know: an older worker during a rolling deploy, or a
            // kind that was removed. Failing it says so on the row instead of looping forever.
            await ledger.FailAsync(job.Id, job.TenantId, "jobs.error.unknownKind", $"No handler for '{job.Kind}' on lane '{lane}'.",
                retryable: false, stopping);
            await msg.AckTerminateAsync(cancellationToken: stopping);
            return;
        }

        using var run = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var context = new LedgerJobContext(ledger, job.Id, job.TenantId, job.Attempts, run.Token);
        var cancelRequested = false;
        var started = Stopwatch.GetTimestamp();

        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(msg, context, () => { cancelRequested = true; run.Cancel(); }, heartbeatStop.Token);

        var succeeded = false;
        try
        {
            var result = await RunHandlerAsync(registration, job, context);
            await heartbeatStop.CancelAsync();
            await heartbeat;
            await context.FlushAsync(stopping);
            await context.WithLedgerAsync(l => l.CompleteAsync(job.Id, job.TenantId,
                result is null ? null : JsonSerializer.Serialize(result, JsonSerializerOptions.Web), stopping), stopping);
            await msg.AckAsync(cancellationToken: stopping);
            succeeded = true;
        }
        catch (OperationCanceledException) when (cancelRequested)
        {
            await StopHeartbeatAsync(heartbeatStop, heartbeat);
            await context.WithLedgerAsync(l => l.MarkCanceledAsync(job.Id, job.TenantId, stopping), stopping);
            await msg.AckAsync(cancellationToken: stopping);
            succeeded = true;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Shutting down mid-run. Hand the job straight back so another replica can take it
            // now, rather than after the lease runs out.
            await StopHeartbeatAsync(heartbeatStop, heartbeat);
            await context.WithLedgerAsync(l => l.ReleaseAsync(job.Id, job.TenantId, CancellationToken.None), CancellationToken.None);
            await msg.NakAsync(cancellationToken: CancellationToken.None);
        }
        catch (JobFailedException ex)
        {
            await StopHeartbeatAsync(heartbeatStop, heartbeat);
            logger.LogWarning(ex, "Job {JobId} ({Kind}) failed: {ErrorKey}.", job.Id, job.Kind, ex.ErrorKey);
            await context.WithLedgerAsync(l => l.FailAsync(job.Id, job.TenantId, ex.ErrorKey, ex.Detail, retryable: false, stopping), stopping);
            await msg.AckAsync(cancellationToken: stopping);
        }
        catch (Exception ex)
        {
            await StopHeartbeatAsync(heartbeatStop, heartbeat);
            logger.LogWarning(ex, "Job {JobId} ({Kind}) failed on attempt {Attempt}.", job.Id, job.Kind, job.Attempts);
            var outcome = await context.WithLedgerAsync(
                l => l.FailAsync(job.Id, job.TenantId, "jobs.error.failed", ex.Message, retryable: true, stopping), stopping);
            if (outcome == JobStatus.Queued)
            {
                // 20s, 40s, 80s … capped: the row is back in Queued, and this redelivery runs it.
                var backoff = TimeSpan.FromSeconds(Math.Min(300, 10 * Math.Pow(2, job.Attempts)));
                await msg.NakAsync(delay: backoff, cancellationToken: stopping);
            }
            else
            {
                await msg.AckAsync(cancellationToken: stopping);
            }
        }
        finally
        {
            metrics.MessageHandled(Streams.Jobs, job.Kind, succeeded, Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>
    /// The handler, in a scope of its own, inside the job's tenant and with the enqueuing request's
    /// audit context restored, so what it records names the person who asked.
    /// </summary>
    private async Task<object?> RunHandlerAsync(JobHandlerRegistration registration, Job job, IJobContext context)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        using var rls = JobLedger.ScopeFor(job.TenantId);

        var header = AuditPropagation.FromJson(job.ContextJson);
        using var activity = DcmsActivitySource.StartLinked($"job {job.Kind}", AuditPropagation.ParentContext(header));
        activity?.SetTag("dcms.job.id", job.Id);
        activity?.SetTag("dcms.job.kind", job.Kind);

        var auditScope = sp.GetService<AuditScope>();
        var ambient = sp.GetService<AuditAmbient>();
        IDisposable? entered = null;
        if (auditScope is not null && ambient is not null)
        {
            AuditPropagation.Restore(auditScope, header, job.TenantId);
            if (activity is not null)
            {
                auditScope.TraceId = activity.TraceId.ToString();
                auditScope.SpanId = activity.SpanId.ToString();
            }
            var actor = sp.GetService<ICurrentActor>();
            if (actor is not null)
            {
                auditScope.ActorResolver ??= () => new AuditActor(actor.Kind, actor.Id, actor.Key, actor.Display, AuditAttribution.Direct);
            }
            entered = ambient.Enter(auditScope);
        }

        try
        {
            var result = await registration.Invoke(sp, job.PayloadJson, context);
            if (sp.GetService<IAuditRecorder>() is { } recorder)
            {
                await recorder.FlushAsync(context.Cancellation);
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            entered?.Dispose();
        }
    }

    private async Task HeartbeatAsync(
        INatsJSMsg<JobRunRequested> msg, LedgerJobContext context, Action stop, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Heartbeat);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await msg.AckProgressAsync(cancellationToken: ct);
                    await context.FlushAsync(ct);
                    if (await context.WithLedgerAsync(l => l.RenewAsync(context.JobId, context.TenantId, ct), ct))
                    {
                        stop();
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One missed beat is harmless: the lease outlives many of them. Keep beating;
                    // stopping here would let another replica reclaim a job that is still running.
                    logger.LogDebug(ex, "Heartbeat for job {JobId} failed; trying again next tick.", context.JobId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The run finished, or the host is stopping.
        }
    }

    private static async Task StopHeartbeatAsync(CancellationTokenSource stop, Task heartbeat)
    {
        await stop.CancelAsync();
        await heartbeat;
    }
}
