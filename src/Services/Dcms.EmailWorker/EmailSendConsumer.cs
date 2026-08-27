using Dcms.Shared.Audit;
using Dcms.Shared.Telemetry;
using Dcms.Shared.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Messaging.Email;
using MailKit.Net.Smtp;
using MimeKit;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.EmailWorker;

/// <summary>
/// Drains the EMAIL work queue and hands each message to the SMTP relay.
///
/// Delivery discipline, all of it leaning on JetStream rather than on process memory:
///  * explicit ack — a message stays on the stream until the relay has taken it, so
///    a worker that dies mid-send has the mail redelivered rather than lost;
///  * NAK with a growing delay — a relay that is down, throttling us, or greylisting
///    (the common case) gets backed off instead of hammered;
///  * MaxDeliver + terminate — a message that keeps failing is dead-lettered with an
///    error log rather than cycling forever and blocking the queue behind it;
///  * a 5xx from the relay is permanent (bad address, rejected sender), so it is
///    terminated on the first attempt: retrying cannot change the answer.
///
/// MaxAckPending is small on purpose. Relays rate-limit, and transactional mail is
/// low volume; a couple in flight keeps latency low without looking like a burst.
/// </summary>
public sealed class EmailSendConsumer(
    INatsJSContext jetStream,
    IEmailSender sender,
    IServiceProvider services,
    DcmsMetrics metrics,
    ILogger<EmailSendConsumer> logger) : BackgroundService
{
    private const string DurableName = "email-sender";

    /// <summary>Attempts per message before it is dead-lettered (~30s, 1m, 2m, 4m, 8m of backoff).</summary>
    private const int MaxDeliver = 6;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrUpdateConsumerAsync(
                    Streams.Email,
                    new ConsumerConfig(DurableName)
                    {
                        FilterSubject = Subjects.EmailSend,
                        AckPolicy = ConsumerConfigAckPolicy.Explicit,
                        // Generous: a slow relay handshake plus send can take a while,
                        // and an ack timeout would send the same mail twice.
                        AckWait = TimeSpan.FromMinutes(2),
                        MaxDeliver = MaxDeliver,
                        MaxAckPending = 2,
                    },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<EmailRequested>(cancellationToken: stoppingToken))
                {
                    await HandleAsync(msg, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Email consumer unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleAsync(INatsJSMsg<EmailRequested> msg, CancellationToken ct)
    {
        var request = msg.Data;
        if (request is null || string.IsNullOrWhiteSpace(request.To))
        {
            // Nothing a retry could fix; drop it so it can't block the queue.
            logger.LogWarning("Discarding unusable email message on {Subject}.", Subjects.EmailSend);
            await msg.AckTerminateAsync(cancellationToken: ct);
            return;
        }

        var attempt = (int)(msg.Metadata?.NumDelivered ?? 1);

        // Restores the person whose action asked for this mail — a password reset, an
        // invitation — so the delivery record names them rather than this worker. The tenant
        // falls back to the payload: nothing here has an ambient one.
        using var serviceScope = services.CreateScope();
        using var context = msg.RestoreAuditContext(serviceScope.ServiceProvider, request.TenantId);
        var audit = serviceScope.ServiceProvider.GetRequiredService<IAuditRecorder>();

        try
        {
            await sender.SendAsync(request.To, request.Subject, request.HtmlBody, request.ReplyTo, ct);
            await msg.AckAsync(cancellationToken: ct);
            Record(audit, request, AuditActions.EmailSent).With("attempt", attempt);
            metrics.EmailSent();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsPermanent(ex))
        {
            // Marks the consumer span, which is what makes the failure visible both in the
            // trace view and in the per-stream message counter.
            context.Failed("rejected permanently by the relay");

            logger.LogError(ex,
                "Dropping {Purpose} email to {Recipient}: rejected permanently by the relay.",
                request.Purpose, request.To);
            await msg.AckTerminateAsync(cancellationToken: ct);
            // The mail will never arrive. Someone waiting on a password reset needs this to be
            // findable, and the log line alone is not somewhere a support request can reach.
            Record(audit, request, AuditActions.EmailFailed).Failed("rejected permanently by the relay");
            // A bounded reason class, never the relay's message — that text is unbounded and
            // routinely quotes the address it refused, which has no business being a label.
            metrics.EmailFailed("rejected");
        }
        catch (Exception ex)
        {
            context.Failed(ex.Message);

            if (attempt >= MaxDeliver)
            {
                logger.LogError(ex,
                    "Giving up on {Purpose} email to {Recipient} after {Attempts} attempts.",
                    request.Purpose, request.To, attempt);
                await msg.AckTerminateAsync(cancellationToken: ct);
                Record(audit, request, AuditActions.EmailFailed)
                    .Failed($"undeliverable after {attempt} attempts")
                    .With("attempts", attempt);
                metrics.EmailFailed("exhausted-retries");
                await audit.FlushAsync(ct);
                return;
            }

            var delay = Backoff(attempt);
            logger.LogWarning(ex,
                "Attempt {Attempt} to send {Purpose} email to {Recipient} failed; retrying in {Delay}.",
                attempt, request.Purpose, request.To, delay);
            await msg.NakAsync(delay: delay, cancellationToken: ct);
        }

        // Explicit: this service has no request pipeline to flush for it, and no database
        // transaction to ride along with. Nothing else will write these.
        await audit.FlushAsync(ct);
    }

    /// <summary>
    /// One record per delivery attempt outcome. The recipient is recorded because an email is
    /// addressed to a person and "we sent it" is not an answer without saying to whom; the body
    /// never is, because it routinely contains a reset token.
    /// </summary>
    private static AuditEntry Record(IAuditRecorder audit, EmailRequested request, string action) =>
        audit.Record(action)
            .As(AuditCategory.System)
            .For("email", request.EventId)
            .With("purpose", request.Purpose)
            .With("recipient", request.To);

    /// <summary>
    /// A 5xx SMTP reply means the relay has made up its mind (unknown mailbox,
    /// sender not permitted); a malformed address never becomes valid either.
    /// Everything else — connection refused, 4xx, timeout — is worth another try.
    /// </summary>
    private static bool IsPermanent(Exception ex) => ex switch
    {
        SmtpCommandException smtp => (int)smtp.StatusCode >= 500,
        ParseException => true,
        _ => false,
    };

    // 30s, 1m, 2m, 4m, 8m.
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(30 * Math.Pow(2, Math.Min(attempt, 5) - 1));
}
