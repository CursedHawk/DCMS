using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Dcms.Shared.Messaging.Email;

/// <summary>
/// The SMTP transport, used only by email-worker as it drains the EMAIL work queue.
/// Services that want to send mail publish to <see cref="IEmailQueue"/> instead —
/// this is the last hop, not the entry point. MailKit-backed; a fresh connection per
/// message is fine for the low volume of transactional mail and keeps the sender stateless.
/// </summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
{
    private readonly EmailOptions _options = options.Value;

    /// <param name="replyTo">
    /// Optional Reply-To. Set for mail whose natural reply target is not the
    /// configured From — e.g. a form notification replying to the visitor.
    /// </param>
    public async Task SendAsync(
        string toAddress,
        string subject,
        string htmlBody,
        string? replyTo = null,
        CancellationToken cancellationToken = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(toAddress));
        if (!string.IsNullOrWhiteSpace(replyTo) && MailboxAddress.TryParse(replyTo, out var replyAddress))
        {
            message.ReplyTo.Add(replyAddress);
        }
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        // Mailpit speaks plain SMTP; real relays usually want STARTTLS.
        var socketOptions = _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(_options.Host, _options.Port, socketOptions, cancellationToken);

        if (!string.IsNullOrEmpty(_options.User))
        {
            await client.AuthenticateAsync(_options.User, _options.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
        logger.LogInformation("Sent email to {Recipient} ({Subject}).", toAddress, subject);
    }
}
