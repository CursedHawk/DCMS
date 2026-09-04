using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.Edge.Routing;

/// <summary>
/// Reloads the edge's route table when the facts it is built from change.
///
/// <para><b>Every replica must see every message</b>, which is why this uses an ephemeral
/// ordered consumer rather than a shared durable. The thing being refreshed is this process's
/// own route table, so a message delivered to a sibling is a reload this replica never performs
/// — and with a shared durable JetStream delivers each message to exactly one consumer. That is
/// correct for a work queue and wrong for a broadcast; site-host was bitten by precisely this,
/// and <c>SiteCacheInvalidator</c> carries the full account.</para>
///
/// <para>Ephemeral rather than durable-per-replica: a durable named after the instance would
/// leave an orphaned consumer in the stream on every recreate, and orphaned consumers hold the
/// stream's ack floor back forever. Nothing here needs to survive a restart — the table is
/// rebuilt from configuration on startup anyway.</para>
///
/// <para>The reload has no visible effect yet: every route is built from configuration, so
/// rebuilding produces the same table. It is wired now because the mechanism is the part worth
/// proving — Phase 2 adds a database-backed route source, and this then becomes the only thing
/// that makes a newly verified domain routable without a deploy.</para>
/// </summary>
public sealed class EdgeConfigInvalidator(
    INatsJSContext jetStream,
    EdgeConfigProvider provider,
    ILogger<EdgeConfigInvalidator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // DeliverPolicy.New: this consumer is created fresh on every start, and
                // replaying the whole tenancy history would only rebuild a table that startup
                // has already built. What matters is everything from now on.
                var consumer = await jetStream.CreateOrderedConsumerAsync(
                    Streams.Tenancy,
                    new NatsJSOrderedConsumerOpts
                    {
                        FilterSubjects = [Subjects.TenantDomainVerified],
                        DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                    },
                    stoppingToken);

                // No ack: an ordered consumer runs with AckPolicy.None. There is nothing to
                // redeliver — a missed reload costs staleness until the next event, and the
                // consumer is recreated from "new" on reconnect anyway.
                await foreach (var msg in consumer.ConsumeAsync<TenantDomainVerified>(cancellationToken: stoppingToken))
                {
                    logger.LogInformation(
                        "Domain {Hostname} verified; reloading edge routes.", msg.Data?.Hostname);
                    provider.Reload();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The edge keeps serving its existing table when NATS is down. That is the whole
                // point of holding the table in memory rather than reading it per request.
                logger.LogWarning(ex, "Edge config invalidator unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
