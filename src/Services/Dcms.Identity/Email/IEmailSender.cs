namespace Dcms.Identity.Email;

/// <summary>Sends transactional email (currently just password-reset links).</summary>
public interface IEmailSender
{
    Task SendAsync(string toAddress, string subject, string htmlBody, CancellationToken cancellationToken = default);
}
