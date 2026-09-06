using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Dcms.Shared.Data;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.Edge.Certificates;

/// <summary>
/// Acts on an operator pressing "renew now" for one of the platform's own certificates, instead
/// of making them wait for the next hourly sweep.
///
/// <para>The durable <c>ReissueRequestedAt</c> flag on the certificate row is what makes the
/// request <i>certain</i> — the sweep honours it whether or not this message arrives. This makes
/// it <i>prompt</i>. A button that appears to do nothing for up to an hour is a button nobody
/// presses a second time.</para>
///
/// <para>Its own consumer rather than a branch inside
/// <see cref="DomainCertificateProvisioner"/>: that one deserialises every message as a
/// <c>TenantDomainVerified</c> and sends the hostname down the per-hostname HTTP-01 path, which
/// is precisely the wrong thing to do with a wildcard.</para>
/// </summary>
public sealed class ManagedCertificateInvalidator(
    INatsJSContext jetStream,
    ManagedCertificateProvisioner managed,
    IOptions<CertificateOptions> options,
    IConfiguration configuration,
    ILogger<ManagedCertificateInvalidator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.TlsEnabled)
        {
            logger.LogInformation("TLS is off; not listening for managed certificate reissue requests.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Ephemeral and ordered, for the reason set out in SiteCacheInvalidator: this is
                // a broadcast to whichever replicas are running, and a shared durable would both
                // deliver it to only one of them and leave an orphaned consumer holding back the
                // stream's ack floor on every container recreate.
                var consumer = await jetStream.CreateOrderedConsumerAsync(
                    Streams.Tenancy,
                    new NatsJSOrderedConsumerOpts
                    {
                        FilterSubjects = [Subjects.ManagedCertificateReissueRequested],
                        DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                    },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<ManagedCertificateReissueRequested>(
                                   cancellationToken: stoppingToken))
                {
                    if (msg.Data is not { } request)
                    {
                        continue;
                    }

                    logger.LogInformation(
                        "Reissue requested for managed certificate '{Name}'; sweeping now.", request.Name);
                    await SweepAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Managed certificate reissue listener unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Sweeps under the same advisory lock the renewal service uses, so this and the hourly pass
    /// can never order the same certificate at once.
    ///
    /// <para><c>TryAcquire</c> and not <c>Acquire</c>: if the hourly sweep is already running it
    /// will pick the flag up on this very pass, so waiting for the lock would only queue a
    /// second identical order behind the first. Skipping is the correct answer, and the flag is
    /// still there if it turns out not to have been.</para>
    /// </summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (connectionString is null)
        {
            logger.LogWarning("No ConnectionStrings:Postgres; leaving the reissue to the hourly sweep.");
            return;
        }

        await using var electionLock = await PostgresAdvisoryLock.TryAcquireAsync(
            connectionString, PostgresAdvisoryLock.EdgeCertificateRenewalLockKey, logger, ct);

        if (electionLock is null)
        {
            logger.LogInformation(
                "A certificate sweep is already running; it will pick this reissue up itself.");
            return;
        }

        var result = await managed.SweepAsync(ct);
        logger.LogInformation(
            "Reissue sweep: {Issued} issued, {Renewed} renewed, {Failed} failed, {Deferred} held "
            + "by a rate-limit ceiling.",
            result.Issued, result.Renewed, result.Failed, result.Deferred);
    }
}
