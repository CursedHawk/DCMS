using Dcms.Shared.Contracts.Events;
using NATS.Client.JetStream;

namespace Dcms.Shared.Messaging;

public sealed class NatsEventPublisher(INatsJSContext jetStream) : IEventPublisher
{
    public async ValueTask PublishAsync<T>(string subject, T @event, CancellationToken ct = default)
        where T : IDcmsEvent
    {
        var ack = await jetStream.PublishAsync(subject, @event, cancellationToken: ct);
        ack.EnsureSuccess();
    }
}
