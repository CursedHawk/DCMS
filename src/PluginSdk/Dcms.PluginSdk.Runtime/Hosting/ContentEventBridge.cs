using System.Text.Json;
using Dcms.PluginSdk.Abstractions;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Runtime.Contracts;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data.Rls;
using Dcms.Shared.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.PluginSdk.Runtime.Hosting;

/// <summary>
/// Turns the platform's <c>content.published</c> / <c>content.unpublished</c> into the owning
/// plugin's typed events (<see cref="ContentTypeDefinition.Published"/>), so a plugin that wants
/// to know when a blog post goes live subscribes to <c>blog.post.published</c> through the Blog
/// contract instead of reading the CMS stream and guessing which instance is a blog.
///
/// <para>Every publish path already converges on those two subjects via the CMS outbox (editor,
/// schedule, import), which is why this bridges the stream rather than hooking an endpoint.</para>
/// </summary>
public sealed class ContentEventBridge(
    PluginRegistry registry,
    IServiceProvider services,
    INatsJSContext jetStream,
    IEventPublisher bus,
    ILogger<ContentEventBridge> logger) : BackgroundService
{
    private const string DurableName = "admin-api-plugin-content-events";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!registry.Manifests.SelectMany(m => m.ContentTypes).Any(t => t.Published is not null || t.Unpublished is not null))
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(Streams.Cms, new ConsumerConfig(DurableName)
                {
                    FilterSubjects = [Subjects.ContentPublished, Subjects.ContentUnpublished],
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    AckWait = TimeSpan.FromMinutes(1),
                    MaxDeliver = 5,
                    DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                }, stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<ContentPublished>(cancellationToken: stoppingToken))
                {
                    try
                    {
                        if (msg.Data is { } evt)
                        {
                            await BridgeAsync(msg.Subject == Subjects.ContentPublished, evt, stoppingToken);
                        }
                        await msg.AckAsync(cancellationToken: stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Could not raise the plugin event for {Subject}.", msg.Subject);
                        await msg.NakAsync(delay: TimeSpan.FromSeconds(10), cancellationToken: stoppingToken);
                    }
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

    /// <summary>
    /// Raises the plugin event for one content message; public for tests. Unpublish messages
    /// share <see cref="ContentPublished"/>'s shape, so both are read as it.
    /// </summary>
    public async Task BridgeAsync(bool published, ContentPublished evt, CancellationToken ct)
    {

        using var scope = services.CreateScope();
        using var rls = RlsScope.Tenant(evt.TenantId);
        var enabled = await scope.ServiceProvider.GetRequiredService<PluginContextFactory>().EnabledInstancesAsync(evt.TenantId, ct);
        var instance = enabled.FirstOrDefault(i => i.InstanceId == evt.PluginInstanceId);
        var type = instance is null
            ? null
            : registry.Find(instance.PluginId)?.ContentTypes.FirstOrDefault(t => t.Name == evt.ContentType);
        var eventType = published ? type?.Published : type?.Unpublished;
        if (instance is null || eventType is null)
        {
            return;
        }

        var name = ContractIds.EventName(eventType);
        var payload = Activator.CreateInstance(eventType, evt.PluginInstanceId, evt.ContentItemId, evt.ContentType, evt.Slug)!;
        var message = new PluginEventPublished(
            Guid.CreateVersion7(), evt.OccurredAt, evt.TenantId, instance.PluginId, name,
            JsonSerializer.Serialize(payload, eventType, ContractDescriptorBuilder.Json));
        // Keyed on the source event, so a redelivered content message is deduplicated by the stream.
        await bus.PublishAsync(Subjects.PluginEvent(instance.PluginId, name), message, ct, messageId: $"{evt.EventId}:{name}");
    }
}
