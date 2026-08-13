namespace Dcms.Shared.Messaging.Email;

/// <summary>
/// A rendered email handed to the queue. The producer owns the wording and markup;
/// the worker only delivers it.
/// </summary>
/// <param name="Recipients">One message is queued per address, so a bounce to one does not re-send to the others.</param>
/// <param name="ReplyTo">Optional Reply-To, e.g. the visitor behind a form submission.</param>
/// <param name="Purpose">Short tag carried into the worker's logs, e.g. "password-reset".</param>
/// <param name="DedupeKey">
/// Optional idempotency key. When set, re-enqueuing the same key+recipient inside the
/// stream's duplicate window is discarded by JetStream instead of sending twice —
/// worth setting whenever the caller has a natural id (a submission id, a token id).
/// </param>
public sealed record EmailMessage(
    IReadOnlyList<string> Recipients,
    string Subject,
    string HtmlBody,
    string? ReplyTo = null,
    string Purpose = "transactional",
    Guid? TenantId = null,
    string? DedupeKey = null);

/// <summary>
/// Publishes transactional email to the EMAIL work queue, where email-worker picks
/// it up and talks to the SMTP relay. Producers never send mail themselves: enqueuing
/// is fast and off the SMTP path, and a relay that is slow, rate-limiting or down
/// costs a retry in the worker instead of a failed HTTP request.
/// </summary>
public interface IEmailQueue
{
    /// <summary>
    /// Enqueues one message per recipient. Throws if the queue is unreachable —
    /// callers for whom mail is best-effort (a notification about work that is
    /// already persisted) should catch and log rather than fail their request.
    /// </summary>
    Task EnqueueAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
