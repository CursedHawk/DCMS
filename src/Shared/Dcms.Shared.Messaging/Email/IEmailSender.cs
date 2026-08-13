namespace Dcms.Shared.Messaging.Email;

/// <summary>
/// The SMTP transport, used only by email-worker as it drains the EMAIL work queue.
/// Services that want to send mail publish to <see cref="IEmailQueue"/> instead —
/// this interface is the last hop, not the entry point.
/// </summary>
public interface IEmailSender
{
    /// <param name="replyTo">
    /// Optional Reply-To. Set for mail whose natural reply target is not the
    /// configured From — e.g. a form notification replying to the visitor.
    /// </param>
    Task SendAsync(
        string toAddress,
        string subject,
        string htmlBody,
        string? replyTo = null,
        CancellationToken cancellationToken = default);
}
