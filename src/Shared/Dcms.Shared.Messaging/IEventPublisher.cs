using Dcms.Shared.Contracts.Events;

namespace Dcms.Shared.Messaging;

/// <summary>
/// Publishes domain events to NATS JetStream. Subjects come from
/// <see cref="Dcms.Shared.Contracts.Messaging.Subjects"/>; payloads are JSON.
/// </summary>
public interface IEventPublisher
{
    ValueTask PublishAsync<T>(string subject, T @event, CancellationToken ct = default)
        where T : IDcmsEvent;
}
