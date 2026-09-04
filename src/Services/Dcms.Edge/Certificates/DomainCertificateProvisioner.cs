using Dcms.Shared.Contracts.Events;
using Dcms.Shared.Contracts.Messaging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Dcms.Edge.Certificates;

/// <summary>
/// Issues a certificate the moment a domain is verified, rather than waiting for a visitor.
///
/// <para><b>This, not the handshake, is the main path.</b> Issuing on demand means the first
/// visitor to a newly verified domain pays for a full ACME round trip inside their TLS
/// handshake, and if anything goes wrong they get a connection error on a site the tenant had
/// just been told was live.
/// Here the certificate normally exists before anyone asks for it, and a failure surfaces on the
/// domain row in the admin UI while the tenant is still looking at the page that caused it.</para>
///
/// <para>A shared durable consumer would be the wrong choice for the same reason as everywhere
/// else on this platform — but note the reason is different here. This work is idempotent and
/// genuinely wants to happen once, not once per replica; it uses an ephemeral ordered consumer
/// anyway because the certificate store's single-flight guard already collapses duplicates, and
/// a durable would leave an orphaned consumer holding back the stream's ack floor on every
/// container recreate.</para>
/// </summary>
public sealed class DomainCertificateProvisioner(
    INatsJSContext jetStream,
    CertificateProvisioner provisioner,
    ICertificateStore store,
    IOptions<CertificateOptions> options,
    ILogger<DomainCertificateProvisioner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.TlsEnabled)
        {
            logger.LogInformation("TLS is off; not pre-issuing certificates for verified domains.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var consumer = await jetStream.CreateOrderedConsumerAsync(
                    Streams.Tenancy,
                    new NatsJSOrderedConsumerOpts
                    {
                        FilterSubjects = [Subjects.TenantDomainVerified],
                        DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                    },
                    stoppingToken);

                await foreach (var msg in consumer.ConsumeAsync<TenantDomainVerified>(cancellationToken: stoppingToken))
                {
                    if (msg.Data?.Hostname is not { Length: > 0 } hostname)
                    {
                        continue;
                    }

                    logger.LogInformation("Domain {Hostname} verified; pre-issuing a certificate.", hostname);
                    // Dropped first, because this subject now also carries "an operator uploaded
                    // a certificate" and "an operator asked for a reissue". The cached entry
                    // expires when the certificate does, so without this the edge would keep
                    // serving the one that was just replaced -- for as long as it had left.
                    // Failures are recorded on the certificate row by the provisioner and are not
                    // rethrown: one domain whose DNS is still propagating must not stop the
                    // consumer and with it every later domain.
                    store.Invalidate(hostname);
                    await provisioner.EnsureAsync(hostname, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Domain certificate provisioner unavailable; retrying in 5s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
