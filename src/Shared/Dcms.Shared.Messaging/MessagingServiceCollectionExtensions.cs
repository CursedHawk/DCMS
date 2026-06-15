using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
}
