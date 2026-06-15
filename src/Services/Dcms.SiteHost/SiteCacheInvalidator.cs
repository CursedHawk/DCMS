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
/// </summary>
public sealed class SiteCacheInvalidator(
    INatsJSContext jetStream,
    IServiceProvider services,
    DomainResolver resolver,
    ILogger<SiteCacheInvalidator> logger) : BackgroundService
{
    private const string DurableName = "site-host-cache";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    Streams.SitesEvents,
                    new ConsumerConfig(DurableName) { AckPolicy = ConsumerConfigAckPolicy.Explicit },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<SitePublished>(cancellationToken: stoppingToken))
                {
                    // Without a domain in the event, clear by re-resolving lazily:
                    // the simplest correct action is to let entries expire, but we
                    // proactively clear known domains for the site's tenant.
                    await InvalidateForSiteAsync(msg.Data, stoppingToken);
                    await msg.AckAsync(cancellationToken: stoppingToken);
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
