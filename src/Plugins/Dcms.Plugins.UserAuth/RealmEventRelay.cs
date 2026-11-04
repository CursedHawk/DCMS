using System.Text.Json;
using Dcms.Plugins.UserAuth.Api;
using Dcms.PluginSdk.Abstractions.Contracts;
using Dcms.PluginSdk.Abstractions.Platform;
using Dcms.PluginSdk.Runtime.Hosting;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.Plugins.UserAuth;

/// <summary>
/// Relays identity's "an account became active" (<see cref="Subjects.RealmUserActivated"/>) as this
/// plugin's <c>user.activated</c> event, in the tenant whose realm it is — so functions and other
/// plugins can act on it. A tenant without the plugin enabled has no one to tell.
/// </summary>
public sealed class RealmEventRelay(INatsJSContext jetStream, PluginHandlerRunner runner, ILogger<RealmEventRelay> logger) : BackgroundService
{
    private const string DurableName = "user-auth-realm-events";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(Streams.Tenancy,
                    new ConsumerConfig(DurableName) { FilterSubject = Subjects.RealmUserActivated, AckPolicy = ConsumerConfigAckPolicy.Explicit },
                    stoppingToken);
                await foreach (var msg in consumer.ConsumeAsync<RealmUserActivated>(cancellationToken: stoppingToken))
                {
                    if (msg.Data is { } activated)
                    {
                        await runner.RunAsync(activated.TenantId, UserAuthPermissions.PluginId, null,
                            (context, _) => context.PublishAsync(new UserActivated(activated.UserId, activated.Email), stoppingToken), stoppingToken);
                    }
                    await msg.AckAsync(cancellationToken: stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realm event relay unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
