using System.Text;
using Dcms.Edge;
using Dcms.Shared.Vault;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates;

/// <summary>
/// Gets the platform's own hostnames a certificate before anybody asks for one, and says loudly
/// when it cannot.
///
/// <para><b>Why this exists.</b> Without it the first certificate for every platform hostname is
/// issued inside a visitor's TLS handshake, bounded by
/// <c>Edge:Certificates:OnDemandTimeoutSeconds</c>. A full ACME order — the CA resolving the
/// name, fetching the HTTP-01 challenge, finalising — routinely takes longer than that, so the
/// handshake is aborted; the failure is then recorded and backed off, and the next attempt is
/// further away than the last. The symptom is <c>ERR_CONNECTION_CLOSED</c> on every host at
/// once, with nothing in the browser to say why, and it does not recover on its own.</para>
///
/// <para>On-demand issuance stays, for tenant domains, where the set is not known in advance.
/// The platform's own names ARE known — they are configuration — so they are issued here, in the
/// background, where taking two minutes costs nobody a page.</para>
///
/// <para>It also probes Vault Transit first. Every private key is encrypted through it, so a
/// Transit grant that was never provisioned means no certificate can be stored or loaded, ever —
/// and the only visible effect is the same silent handshake abort. One log line naming Vault is
/// the difference between an hour of diagnosis and a minute of it.</para>
/// </summary>
public sealed class EdgeTlsPreflight(
    IServiceProvider services,
    CertificateProvisioner provisioner,
    ICertificateStore store,
    IOptions<CertificateOptions> certificates,
    IOptions<EdgeOptions> edge,
    ILogger<EdgeTlsPreflight> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!certificates.Value.TlsEnabled)
        {
            return;
        }

        // Deliberately not blocking startup. The edge serves every route from its static table
        // without a single certificate, and refusing to start would turn "TLS is not ready yet"
        // into "the ingress is down".
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        if (!await TransitWorksAsync(stoppingToken))
        {
            // Nothing below can succeed, so there is no point attempting it and filling the log
            // with ACME failures that all have one cause.
            return;
        }

        // The store works. Anything with issuance still owed to it -- no certificate at all, or
        // a reissue an operator asked for -- gets its backoff cleared, so a restart after fixing
        // the cause is enough. See ClearBackoffForOutstandingWorkAsync for why an operator would
        // otherwise watch a repaired platform stay dark for hours.
        var cleared = await store.ClearBackoffForOutstandingWorkAsync(stoppingToken);
        if (cleared > 0)
        {
            logger.LogInformation(
                "Cleared the failure backoff on {Count} hostnames with issuance outstanding; "
                + "they will be attempted on this pass.", cleared);
        }

        foreach (var hostname in edge.Value.PlatformHostnames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (await provisioner.EnsureAsync(hostname, stoppingToken) is not null)
                {
                    logger.LogInformation("Certificate ready for {Hostname}.", hostname);
                    continue;
                }

                // The three causes, in the order they actually occur, because the CA's own error
                // is already on the certificate row and this is the line an operator sees first.
                logger.LogError(
                    "No certificate for {Hostname}, so TLS handshakes for it will be REFUSED. "
                    + "Usually: no DNS A record pointing here yet, port 80 not reachable from the "
                    + "internet for the HTTP-01 challenge, or the ACME account being rate-limited. "
                    + "See edge.certificates.LastError for what the CA said.",
                    hostname);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Certificate provisioning failed for {Hostname}.", hostname);
            }
        }
    }

    private async Task<bool> TransitWorksAsync(CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var transit = scope.ServiceProvider.GetRequiredService<ITransitEncryptor>();
            var ciphertext = await transit.EncryptAsync(
                VaultTransitServiceCollectionExtensions.TlsKeysKey,
                Encoding.UTF8.GetBytes("edge-preflight"), ct);
            var roundTripped = Encoding.UTF8.GetString(
                await transit.DecryptAsync(VaultTransitServiceCollectionExtensions.TlsKeysKey, ciphertext, ct));

            if (roundTripped == "edge-preflight")
            {
                return true;
            }

            logger.LogError("Vault Transit round-trip returned unexpected data; certificates cannot be stored.");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "TLS IS ENABLED BUT VAULT TRANSIT IS UNUSABLE, so no certificate can be stored or "
                + "read and every handshake will be refused. Check that VAULT_ROLE_ID_EDGE and "
                + "VAULT_SECRET_ID_EDGE are set, and that infra/vault/apply.sh has been run so the "
                + "'{Key}' transit key and the dcms-edge policy exist.",
                VaultTransitServiceCollectionExtensions.TlsKeysKey);
            return false;
        }
    }
}
