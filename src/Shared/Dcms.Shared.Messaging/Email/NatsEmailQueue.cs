using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Microsoft.Extensions.Logging;

namespace Dcms.Shared.Messaging.Email;

/// <summary>
/// JetStream-backed <see cref="IEmailQueue"/>. Publishing is acknowledged by the
/// server before this returns, so an enqueued message is on disk and survives a
/// restart of both the producer and the worker.
/// </summary>
public sealed class NatsEmailQueue(IEventPublisher events, ILogger<NatsEmailQueue> logger) : IEmailQueue
{
    public async Task EnqueueAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        foreach (var recipient in message.Recipients)
        {
            if (string.IsNullOrWhiteSpace(recipient))
            {
                continue;
            }

            var address = recipient.Trim();
            var requested = new EmailRequested(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                address,
                message.Subject,
                message.HtmlBody,
                message.ReplyTo,
                message.Purpose,
                message.TenantId);

            await events.PublishAsync(
                Subjects.EmailSend,
                requested,
                cancellationToken,
                messageId: message.DedupeKey is null ? null : $"{message.DedupeKey}:{address}");

            logger.LogDebug("Queued {Purpose} email to {Recipient}.", message.Purpose, address);
        }
    }
}
