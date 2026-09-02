using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Telemetry;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.AdminApi.Notifications;

/// <summary>
/// Turns one JetStream subject into notifications. Subclasses supply the stream, subject,
/// durable name and a mapping from the event to a <see cref="NotificationRequest"/>;
/// everything else — the reconnect loop, per-message scope, ack/nak — lives here.
///
/// <para><b>These are shared durables, deliberately.</b> The neighbouring
/// <c>SiteCacheInvalidator</c> uses an ephemeral <i>ordered</i> consumer so that every
/// replica sees every message, because it mutates per-replica in-memory state. Notifications
/// are the opposite: the work is a database write, so exactly one replica must do it, and
/// the SignalR Redis backplane is what gets the push to browsers connected elsewhere. A
/// per-replica consumer here would insert N copies of every notification.</para>
///
/// <para>Subclasses must only ever consume the <c>*_EVENTS</c> streams. <c>SITES</c> and
/// <c>MEDIA</c> are work-queue retention: a message is removed once acked and only one
/// consumer per subject filter is allowed, so a consumer added there would steal jobs from
/// site-builder and media-worker.</para>
/// </summary>
public abstract class NotificationConsumerBase<TEvent>(
    IServiceProvider services,
    INatsJSContext jetStream,
    DcmsMetrics metrics,
    ILogger logger) : BackgroundService
    where TEvent : class, IDcmsEvent
{
    protected abstract string Stream { get; }
    protected abstract string Subject { get; }
    protected abstract string DurableName { get; }

    /// <summary>
    /// Builds the notification for this event, or returns null to skip it (a sandbox
    /// preview, an event the tenant should not be told about). The scope is per-message, so
    /// resolving names from other DbContexts here is safe.
    /// </summary>
    protected abstract Task<NotificationRequest?> MapAsync(
        TEvent evt, IServiceProvider scope, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    Stream,
                    new ConsumerConfig(DurableName)
                    {
                        FilterSubject = Subject,
                        AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<TEvent>(cancellationToken: stoppingToken))
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

    private async Task HandleAsync(INatsJSMsg<TEvent> msg, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (msg.Data is { } evt)
            {
                using var scope = services.CreateScope();
                var request = await MapAsync(evt, scope.ServiceProvider, ct);
                if (request is not null)
                {
                    var publisher = scope.ServiceProvider.GetRequiredService<INotificationPublisher>();
                    await publisher.RaiseAsync(request, ct);
                }
            }
            await msg.AckAsync(cancellationToken: ct);
            metrics.MessageHandled(Stream, Subject, succeeded: true, started.Elapsed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Durable} failed handling a message; will redeliver.", DurableName);
            metrics.MessageHandled(Stream, Subject, succeeded: false, started.Elapsed);
            await msg.NakAsync(cancellationToken: ct);
        }
    }
}
