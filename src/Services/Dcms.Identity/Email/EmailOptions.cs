namespace Dcms.Identity.Email;

/// <summary>
/// SMTP delivery settings, bound from the "Email" configuration section. Defaults
/// target the Mailpit container that runs in every environment (dev and vps),
/// which captures mail without delivering it. Point Host/Port/User/Password at a
/// real relay in production to actually deliver reset links to users.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string Host { get; set; } = "mailpit";
    public int Port { get; set; } = 1025;

    /// <summary>Enable STARTTLS. Off for Mailpit; on for most real relays.</summary>
    public bool UseStartTls { get; set; }

    public string? User { get; set; }
    public string? Password { get; set; }

    public string FromAddress { get; set; } = "no-reply@dcms.local";
    public string FromName { get; set; } = "DCMS";
}
