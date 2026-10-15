using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.Shared.Jobs;

/// <summary>
/// Folds <c>jobs.state</c> reports from bus-only workers into the ledger. One shared durable: the
/// work is a database write, so exactly one replica should do each — the same shape as
/// <c>NotificationIngestConsumer</c> and for the same reason.
///
/// <para>Serial on purpose. Reports are already coalesced to one a second per job, each one is a
/// couple of primary-key updates, and in-order handling keeps a job's ticks in the order they
/// were sent.</para>
/// </summary>
public sealed class JobStateIngestConsumer(
    IServiceProvider services,
    INatsJSContext jetStream,
    ILogger<JobStateIngestConsumer> logger) : BackgroundService
{
    private const string DurableName = "job-state-ingest";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(Streams.JobsEvents, new ConsumerConfig(DurableName)
                {
                    FilterSubject = Subjects.JobState,
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    AckWait = TimeSpan.FromSeconds(30),
                    MaxDeliver = 5,
                }, stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<JobStateReported>(cancellationToken: stoppingToken))
                {
                    await HandleAsync(msg, stoppingToken);
                }
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

    private async Task HandleAsync(INatsJSMsg<JobStateReported> msg, CancellationToken ct)
    {
        if (msg.Data is not { } report)
        {
            await msg.AckTerminateAsync(cancellationToken: ct);
            return;
        }
        try
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<JobLedger>().ApplyReportAsync(report, ct);
            await msg.AckAsync(cancellationToken: ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not apply a report for job {JobId}; retrying.", report.JobId);
            await msg.NakAsync(delay: TimeSpan.FromSeconds(5), cancellationToken: ct);
        }
    }
}
