using Dcms.Shared.Audit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.Serializers.Json;
using NATS.Net;

namespace Dcms.Shared.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the NATS connection, JetStream context, and event publisher.
    /// Connection string comes from "Nats:Url" (default nats://localhost:4222).
    /// </summary>
    public static IServiceCollection AddDcmsMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["Nats:Url"] ?? "nats://localhost:4222";

        services.AddSingleton<INatsConnection>(_ => new NatsConnection(NatsOpts.Default with
        {
            Url = url,
            SerializerRegistry = NatsJsonSerializerRegistry.Default,
            Name = "dcms",
        }));
        services.AddSingleton<INatsJSContext>(sp => sp.GetRequiredService<INatsConnection>().CreateJetStreamContext());
        services.AddSingleton<IEventPublisher, NatsEventPublisher>();

        services.AddHealthChecks().AddCheck<NatsHealthCheck>("nats", tags: ["ready"]);

        return services;
    }

    /// <summary>
    /// Sends this service's audit records over JetStream instead of the local outbox.
    ///
    /// <para>Only for the two services that cannot reach the <c>audit</c> schema — email-worker
    /// and site-builder. Everywhere else <c>AddDcmsAuditData</c> is correct and strictly
    /// stronger: it writes the record in the same transaction as the change. Calling this in a
    /// service that could have used the outbox would trade an atomic write for an
    /// at-least-once one and gain nothing.</para>
    /// </summary>
    public static IServiceCollection AddDcmsAuditOverNats(this IServiceCollection services)
    {
        services.TryAddSingleton<AuditEventJson>();

        // Replaces the logging floor registered by AddDcmsAudit.
        services.RemoveAll<IAuditSink>();
        services.AddSingleton<IAuditSink, NatsAuditSink>();

        return services;
    }
}
