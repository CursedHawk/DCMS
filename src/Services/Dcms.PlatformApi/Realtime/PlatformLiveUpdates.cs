using System.Text.Json;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.PlatformApi.Realtime;

/// <summary>
/// Turns the handful of platform-wide events into refetch hints on every open console.
///
/// <para><b>An ordered ephemeral consumer, delivering only what is new</b> — the same shape
/// site-host uses for the very same events, and for the same two reasons. Every replica must
/// see every message, because each one holds its own set of WebSocket connections; and nothing
/// here should replay a backlog on restart, because a refetch hint about a tenant that was
/// suspended an hour ago is noise. A durable consumer would give exactly the wrong behaviour on
/// both counts.</para>
///
/// <para><b>Why platform-api consumes rather than admin-api pushing.</b> The hub lives here
/// because this is the console's API — the same reasoning ADR 0009 used to keep the tenant hub
/// out of content-api. admin-api would otherwise need an <c>IHubContext</c> for a hub it does
/// not host, which works only by both services agreeing on a Redis channel prefix: a coupling
/// with nothing in the type system holding it together.</para>
///
/// <para>Failure here is bounded on purpose. If NATS is unreachable the loop retries and the
/// console falls back to the polls it still has; nothing waits on this and nothing breaks
/// without it.</para>
/// </summary>
public sealed class PlatformLiveUpdates(
    INatsJSContext jetStream,
    IPlatformChangePublisher changes,
    ILogger<PlatformLiveUpdates> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrderedConsumerAsync(
                    Streams.Tenancy,
                    new NatsJSOrderedConsumerOpts
                    {
                        DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                        // TENANCY also carries membership, plugin-instance and certificate
                        // reissue messages, none of which changes anything this console shows.
                        FilterSubjects =
                        [
                            Subjects.TenantCreated,
                            Subjects.TenantSuspended,
                            Subjects.TenantResumed,
                            Subjects.PlatformNotificationRaised,
                        ],
                    },
                    stoppingToken);

                // Deserialized as JsonElement rather than into one record type: four subjects
                // with three different payload shapes share this consumer, and it reads two
                // fields out of them between the lot.
                await foreach (var msg in consumer.ConsumeAsync<JsonElement>(
                    cancellationToken: stoppingToken))
                {
                    await HandleAsync(msg.Subject, msg.Data, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Platform live updates unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Read case-insensitively rather than assuming the wire casing.
    ///
    /// <para>The publisher serialises with System.Text.Json's defaults, so these arrive
    /// PascalCase today — but that is a property of the shared serializer registry rather than
    /// of the contract, and a consumer that silently reads nothing when it changes is a
    /// consumer that goes quiet without failing. Case-insensitive costs a comparison and
    /// removes the trap.</para>
    /// </summary>
    private static readonly JsonSerializerOptions Wire =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>Only the fields this consumer reads. Every tenant event carries them.</summary>
    private sealed record TenantEvent(Guid TenantId);

    private async Task HandleAsync(string subject, JsonElement body, CancellationToken ct)
    {
        if (subject == Subjects.PlatformNotificationRaised)
        {
            var raised = body.Deserialize<PlatformNotificationRaised>(Wire);

            // The bell always. The page the news is about as well, when the kind implies one --
            // a certificate failure changes both the badge and the certificates table, and an
            // operator watching the table should not have to notice the badge to see it.
            await changes.PublishAsync(PlatformResourceTags.Notifications, ct: ct);
            if (raised is not null && PlatformResourceTags.ForKind(raised.Kind) is { } tag)
            {
                await changes.PublishAsync(tag, raised.NotificationId.ToString(), ct);
            }
            return;
        }

        var tenant = body.Deserialize<TenantEvent>(Wire);
        await changes.PublishAsync(PlatformResourceTags.Tenants, tenant?.TenantId.ToString(), ct);
    }
}
