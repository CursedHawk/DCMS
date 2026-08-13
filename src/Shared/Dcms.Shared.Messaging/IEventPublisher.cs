using Dcms.Shared.Contracts.Events;

namespace Dcms.Shared.Messaging;

/// <summary>
/// Publishes domain events to NATS JetStream. Subjects come from
/// <see cref="Dcms.Shared.Contracts.Messaging.Subjects"/>; payloads are JSON.
/// </summary>
public interface IEventPublisher
{
    /// <param name="messageId">
    /// Optional idempotency key (published as Nats-Msg-Id). JetStream discards a
    /// duplicate id seen inside the stream's dupe window, so a publisher that
    /// retries after an ambiguous failure does not enqueue the work twice.
    /// </param>
    ValueTask PublishAsync<T>(string subject, T @event, CancellationToken ct = default, string? messageId = null)
        where T : IDcmsEvent;
}
