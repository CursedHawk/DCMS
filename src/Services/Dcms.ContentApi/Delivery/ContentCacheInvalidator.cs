using Dcms.Shared.Caching;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.ContentApi.Delivery;

/// <summary>
/// Invalidates the Redis content cache on content.published / content.unpublished:
/// deletes the item key and bumps the per-instance generation counter (which
/// invalidates all cached lists). Resilient to NATS being unavailable.
/// </summary>
public sealed class ContentCacheInvalidator(
    INatsJSContext jetStream,
    ICacheService cache,
    ILogger<ContentCacheInvalidator> logger) : BackgroundService
{
    private const string DurableName = "content-api-cache-invalidation";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    Streams.Cms,
                    new ConsumerConfig(DurableName) { AckPolicy = ConsumerConfigAckPolicy.Explicit },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<ContentInvalidation>(cancellationToken: stoppingToken))
                {
                    try
                    {
                        if (msg.Data is { } e)
                        {
                            await cache.RemoveAsync(
                                PublishedContentReader.ItemKey(e.TenantId, e.PluginInstanceId, e.ContentType, e.Slug), stoppingToken);
                            await cache.IncrementAsync(
                                PublishedContentReader.GenKey(e.TenantId, e.PluginInstanceId), stoppingToken);
                        }
                        await msg.AckAsync(cancellationToken: stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed handling content cache invalidation; will redeliver.");
                        await msg.NakAsync(cancellationToken: stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Content cache invalidator unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    // Shared shape of ContentPublished/ContentUnpublished — only the fields the
    // invalidator needs (tolerant to either event on the CMS stream).
    private sealed record ContentInvalidation(
        Guid TenantId, Guid PluginInstanceId, string ContentType, string Slug);
}
