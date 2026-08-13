namespace Dcms.Shared.Contracts.Events;

/// <summary>
/// One transactional email waiting to be delivered, published to the EMAIL work
/// queue. Every service that decides mail should go out publishes this; only
/// email-worker consumes it, so it is the single process that speaks SMTP and the
/// single place relay credentials live.
///
/// Deliberately one recipient per message: the queue redelivers a failed message,
/// and a multi-recipient payload would re-send to the addresses that already
/// succeeded. Producers fan out (see Dcms.Shared.Messaging.Email.IEmailQueue).
///
/// The body is rendered by the producer, not the worker — the worker knows nothing
/// about password resets or form submissions, only how to hand a message to a relay.
/// </summary>
/// <param name="To">Single recipient address.</param>
/// <param name="ReplyTo">Optional Reply-To, for mail whose natural reply target is not the configured From.</param>
/// <param name="Purpose">Short tag for logs/diagnostics, e.g. "password-reset" or "form-notification".</param>
/// <param name="TenantId">Owning tenant, when the mail was triggered by tenant activity.</param>
public sealed record EmailRequested(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string To,
    string Subject,
    string HtmlBody,
    string? ReplyTo,
    string Purpose,
    Guid? TenantId) : IDcmsEvent
{
    public int Version => 1;
}
