using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.SiteHost;

/// <summary>
/// Drops the route cache for a tenant's domains the moment it is suspended or resumed.
///
/// <para><b>Why this is not optional.</b> <see cref="DomainResolver"/> refuses to route a
/// suspended tenant, but it caches for five minutes. Without an invalidation signal a
/// suspension would take effect somewhere between instantly and five minutes later, differing
/// per replica — so an operator who suspends a tenant and then loads its site sees it still
/// serving and cannot tell whether the feature is broken or merely slow. A resume has the same
/// problem in reverse, and that one is worse: the tenant is paying again and their site is
/// still dark.</para>
///
/// <para>A second consumer rather than an extension of <see cref="SiteCacheInvalidator"/>
/// because these events live on a different stream (TENANCY, not SITES_EVENTS) and carry a
/// different payload. The ordered-ephemeral reasoning is identical and is written out there:
/// the cache is per-process memory, so every replica must see every message.</para>
/// </summary>
public sealed class TenantStatusInvalidator(
    INatsJSContext jetStream,
    IServiceProvider services,
    DomainResolver resolver,
    ILogger<TenantStatusInvalidator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // FilterSubjects matters here in a way it does not for the sites consumer:
                // TENANCY also carries tenant.created, membership.> and plugin.instance.>,
                // none of which deserialize as TenantStatusChanged.
                var consumer = await jetStream.CreateOrderedConsumerAsync(
                    Streams.Tenancy,
                    new NatsJSOrderedConsumerOpts
                    {
                        DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                        FilterSubjects = [Subjects.TenantSuspended, Subjects.TenantResumed],
                    },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<TenantStatusChanged>(
                    cancellationToken: stoppingToken))
                {
                    await InvalidateAsync(msg.Data, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Tenant status invalidator unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task InvalidateAsync(TenantStatusChanged? evt, CancellationToken ct)
    {
        if (evt is null)
        {
            return;
        }

        using var scope = services.CreateScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        // Every domain of the tenant, not only the ones linked to a site: an unlinked domain
        // has no cache entry, and removing one that is not there costs nothing. Missing one
        // that is there would leave a suspended site serving.
        var hosts = await tenancy.Domains.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.TenantId == evt.TenantId)
            .Select(d => d.Hostname)
            .ToListAsync(ct);

        foreach (var host in hosts)
        {
            resolver.Invalidate(host);
        }

        logger.LogInformation(
            "Tenant {Slug} {Status}; dropped {Count} cached route(s).",
            evt.Slug,
            evt.Suspended ? "suspended" : "resumed",
            hosts.Count);
    }
}
