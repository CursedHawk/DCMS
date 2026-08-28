using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.SiteHost;

/// <summary>
/// On site.published / tenant.domain.verified, drops the in-memory route cache
/// for affected domains so the new active build is picked up promptly. Resilient
/// to NATS being unavailable.
///
/// <para><b>Every replica must see every message</b>, which is why this uses an ephemeral
/// ordered consumer rather than a shared durable. The cache being invalidated is in this
/// process's own memory, so an invalidation delivered to a sibling is an invalidation this
/// replica never performs.</para>
///
/// <para>With the previous shared durable (<c>site-host-cache</c>), JetStream delivered each
/// <c>site.published</c> to exactly one replica -- correct for work queues, wrong for a
/// broadcast. The other N-1 replicas kept serving the previous <c>ArtifactPrefix</c> until the
/// five-minute TTL in <see cref="DomainResolver"/> expired, so a publish appeared to take
/// effect immediately or five minutes later depending on which replica the request reached.</para>
///
/// <para>Ephemeral rather than a durable-per-replica: a durable named after the instance would
/// leave an orphaned consumer in the stream on every recreate, and orphaned consumers hold back
/// the stream's ack floor forever. The server cleans an ephemeral up once this process stops
/// pulling. Nothing needs to survive a restart -- an empty cache is a correct cache.</para>
/// </summary>
public sealed class SiteCacheInvalidator(
    INatsJSContext jetStream,
    IServiceProvider services,
    DomainResolver resolver,
    ILogger<SiteCacheInvalidator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // DeliverPolicy.New: this consumer is created fresh on every start, and
                // replaying the whole publish history would only invalidate cache entries that
                // do not exist yet. What matters is everything from now on.
                var consumer = await jetStream.CreateOrderedConsumerAsync(
                    Streams.SitesEvents,
                    new NatsJSOrderedConsumerOpts { DeliverPolicy = ConsumerConfigDeliverPolicy.New },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<SitePublished>(cancellationToken: stoppingToken))
                {
                    // Without a domain in the event, clear by re-resolving lazily:
                    // the simplest correct action is to let entries expire, but we
                    // proactively clear known domains for the site's tenant.
                    //
                    // No ack: an ordered consumer runs with AckPolicy.None. There is nothing to
                    // redeliver -- a missed invalidation costs at most one TTL of staleness, and
                    // the consumer is recreated from "new" on reconnect anyway.
                    await InvalidateForSiteAsync(msg.Data, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Site cache invalidator unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task InvalidateForSiteAsync(SitePublished? evt, CancellationToken ct)
    {
        if (evt is null)
        {
            return;
        }
        using var scope = services.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var hosts = await tenancy.Domains.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.SiteId == evt.SiteId)
            .Select(d => d.Hostname)
            .ToListAsync(ct);
        foreach (var host in hosts)
        {
            resolver.Invalidate(host);
        }
    }
}
