using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Microsoft.AspNetCore.SignalR;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.PlatformApi.Realtime;

/// <summary>
/// Pushes every <c>jobs.changed</c> — every tenant's and the platform's own — to open consoles as
/// <c>JobChanged</c> (ADR 0024). Ordered and ephemeral like <see cref="PlatformLiveUpdates"/>,
/// on its own stream, so each replica sees each change for the sockets it holds.
///
/// <para>Only to <see cref="PlatformHub.JobsGroup"/>: operators who may read jobs. A change names
/// its tenant, and which tenants are busy is not for every holder of some console permission.</para>
///
/// <para>Not coalesced: the ledger already writes a running job at most once a second, so this is
/// bounded by how many jobs run at once, not by how fast they tick.</para>
/// </summary>
public sealed class PlatformJobLiveUpdates(
    INatsJSContext jetStream,
    IHubContext<PlatformHub> hub,
    ILogger<PlatformJobLiveUpdates> logger) : BackgroundService
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
                    // Nobody watching: nothing to do, and no reason to pay for the send.
                    if (msg.Data is not { } e || !PlatformHub.AnyConnected)
                    {
                        continue;
                    }
                    try
                    {
                        await hub.Clients.Group(PlatformHub.JobsGroup).SendAsync("JobChanged",
                            new { id = e.JobId, e.TenantId, e.ParentId, e.Status, e.Done, e.Total, e.StageKey, e.Seq },
                            stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Pushing job {JobId} to the console failed.", e.JobId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Console job updates unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
