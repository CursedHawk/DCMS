using Dcms.AdminApi.Notifications;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Microsoft.AspNetCore.SignalR;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.AdminApi.Jobs;

/// <summary>
/// Pushes <c>jobs.changed</c> to every console open on the job's tenant, as <c>JobChanged</c> on
/// the notification hub (ADR 0024).
///
/// <para>A shared durable consumer: one replica takes each change, and the SignalR Redis
/// backplane carries the push to the sockets held by every replica. It used to be an ordered
/// consumer per replica on the reasoning that each replica pushes only to its own sockets, but a
/// group send through <see cref="IHubContext{THub}"/> goes over the backplane to all of them, so N
/// replicas pushed every change N times (ADR 0036, for running more than one). A durable created
/// now starts at the head of the stream: a progress tick from before it existed is noise.</para>
///
/// <para>The message carries ids and counts, never a title: everyone on the tenant group gets
/// it, including members who may not see the job. What it reveals — that some job moved — their
/// next request answers anyway, and the SPA fetches a job it does not know before showing it.</para>
/// </summary>
public sealed class JobLiveUpdates(
    INatsJSContext jetStream,
    IHubContext<NotificationHub> hub,
    ILogger<JobLiveUpdates> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(Streams.JobsEvents, Consumer(), stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<JobChanged>(cancellationToken: stoppingToken))
                {
                    if (msg.Data is { TenantId: { } tenantId } changed)
                    {
                        await PushAsync(tenantId, changed, stoppingToken);
                    }
                    await msg.AckAsync(cancellationToken: stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Job live updates unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private const string DurableName = "admin-api-job-live-updates";

    /// <summary>What every start sends: identical each time, so updating the existing durable is a no-op.</summary>
    public static ConsumerConfig Consumer() => new(DurableName)
    {
        FilterSubject = Subjects.JobChanged,
        AckPolicy = ConsumerConfigAckPolicy.Explicit,
        // A push is worth one retry at most: the next change supersedes it.
        MaxDeliver = 2,
        // Immutable once the durable exists, so it must be sent unchanged every time:
        // left out, it defaults to All and the server refuses the update ("deliver
        // policy can not be updated"), which stopped every push after the first restart.
        DeliverPolicy = ConsumerConfigDeliverPolicy.New,
    };

    private async Task PushAsync(Guid tenantId, JobChanged changed, CancellationToken ct)
    {
        try
        {
            await hub.Clients.Group(NotificationHub.TenantGroup(tenantId)).SendAsync("JobChanged", JobChangedPush.From(changed), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Pushing job {JobId} to tenant {TenantId} failed.", changed.JobId, tenantId);
        }
    }
}

/// <summary>What a browser receives. Mirrored by the SPAs' <c>JobChanged</c> type.</summary>
public sealed record JobChangedPush(
    Guid Id, Guid? TenantId, Guid? ParentId, string Status, long Done, long? Total, string? StageKey, long Seq)
{
    public static JobChangedPush From(JobChanged e) =>
        new(e.JobId, e.TenantId, e.ParentId, e.Status, e.Done, e.Total, e.StageKey, e.Seq);
}
