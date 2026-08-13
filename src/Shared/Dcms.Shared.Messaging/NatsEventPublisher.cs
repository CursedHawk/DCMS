using Dcms.Shared.Contracts.Events;
using NATS.Client.JetStream;

namespace Dcms.Shared.Messaging;

public sealed class NatsEventPublisher(INatsJSContext jetStream) : IEventPublisher
{
    public async ValueTask PublishAsync<T>(string subject, T @event, CancellationToken ct = default, string? messageId = null)
        where T : IDcmsEvent
    {
        var opts = messageId is null ? null : new NatsJSPubOpts { MsgId = messageId };
        var ack = await jetStream.PublishAsync(subject, @event, opts: opts, cancellationToken: ct);
        ack.EnsureSuccess();
    }
}
