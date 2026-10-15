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
/// <para>Ordered and ephemeral, delivering only what is new — the same shape as platform-api's
/// <c>PlatformLiveUpdates</c> and for the same reasons: each replica holds its own sockets, so
/// every replica must see every change, and a progress tick from before a restart is noise. The
/// Redis backplane is not involved; the replica that holds a socket is the one that hears.</para>
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
                var consumer = await jetStream.CreateOrderedConsumerAsync(
                    Streams.JobsEvents,
                    new NatsJSOrderedConsumerOpts
                    {
                        DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                        FilterSubjects = [Subjects.JobChanged],
                    },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<JobChanged>(cancellationToken: stoppingToken))
                {
                    if (msg.Data is { TenantId: { } tenantId } changed)
                    {
                        await PushAsync(tenantId, changed, stoppingToken);
                    }
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
