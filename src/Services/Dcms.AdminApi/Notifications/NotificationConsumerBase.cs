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
    /// How far back an event may have happened and still be worth a notification.
    ///
    /// <para>The <c>*_EVENTS</c> streams keep seven days, and a durable created today starts at
    /// the beginning of the stream — so the first deploy of a new consumer turns a week of
    /// history into a bellful of notifications about things the tenant already knows. That is
    /// exactly what happened when these consumers first shipped.</para>
    ///
    /// <para><see cref="ConsumerConfigDeliverPolicy.New"/> below prevents it for a consumer
    /// created from now on, but delivery policy is immutable on an existing durable, so this
    /// guard is what protects the ones already out there. It also covers the case the policy
    /// cannot: a long admin-api outage, where the backlog drains all at once and week-old
    /// events would arrive looking current. A day is generous enough that an overnight outage
    /// still delivers everything it should.</para>
    /// </summary>
    protected virtual TimeSpan MaxEventAge => TimeSpan.FromDays(1);

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
                var config = new ConsumerConfig(DurableName)
                {
                    FilterSubject = Subject,
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                };

                // DeliverPolicy is immutable once a durable exists, and sending it on an update
                // is an error -- so it is set only when this consumer is genuinely being
                // created. A new one starts at the head of the stream rather than replaying a
                // week of history into the bell; an existing one keeps whatever it has and
                // relies on the MaxEventAge guard instead.
                if (!await ConsumerExistsAsync(stoppingToken))
                {
                    config.DeliverPolicy = ConsumerConfigDeliverPolicy.New;
                }

                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    Stream, config, stoppingToken);

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
            if (msg.Data is { } evt && !IsStale(evt))
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

    /// <summary>
    /// Whether this durable is already on the server. Any failure is treated as "not there":
    /// the caller only uses the answer to decide whether it may set an immutable field, and
    /// <c>CreateOrUpdateConsumerAsync</c> rejects that attempt on its own if the guess is wrong
    /// — which the retry loop then handles like any other startup failure.
    /// </summary>
    private async Task<bool> ConsumerExistsAsync(CancellationToken ct)
    {
        try
        {
            await jetStream.GetConsumerAsync(Stream, DurableName, ct);
            return true;
        }
        catch (NatsJSApiException)
        {
            return false;
        }
    }

    /// <summary>
    /// Drops an event that describes something too long ago to be news. Acked, not nak'd — it
    /// was handled, in the sense that the decision not to notify is final. See
    /// <see cref="MaxEventAge"/> for why this is needed on top of the delivery policy.
    /// </summary>
    private bool IsStale(TEvent evt)
    {
        var age = DateTimeOffset.UtcNow - evt.OccurredAt;
        if (age <= MaxEventAge)
        {
            return false;
        }
        logger.LogDebug(
            "{Durable} skipping {Subject} from {Age} ago; older than the {MaxAge} notification window.",
            DurableName, Subject, age, MaxEventAge);
        return true;
    }
}
