using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dcms.Shared.Messaging.Email;

public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Producer side: registers <see cref="IEmailQueue"/> so a service can hand mail
    /// to the EMAIL work queue. Needs <c>AddDcmsMessaging</c> for the JetStream
    /// connection, and deliberately registers no SMTP client — only email-worker
    /// holds relay credentials. Safe to call from more than one host.
    /// </summary>
    public static IServiceCollection AddDcmsEmailQueue(this IServiceCollection services)
    {
        services.TryAddSingleton<IEmailQueue, NatsEmailQueue>();
        return services;
    }

    /// <summary>
    /// Consumer side: registers the SMTP transport from the "Email" configuration
    /// section (defaults to the Mailpit container). Only email-worker calls this —
    /// everything else queues through <see cref="AddDcmsEmailQueue"/>.
    /// </summary>
    public static IServiceCollection AddDcmsEmailSender(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.TryAddSingleton<IEmailSender, SmtpEmailSender>();
        return services;
    }
}
