using System.Text;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates;

/// <summary>
/// Obtains certificates from an ACME certificate authority over HTTP-01.
///
/// <para>HTTP-01 rather than TLS-ALPN-01 or DNS-01: the edge already owns port 80 (it has to,
/// for the HTTPS redirect), the challenge is a plain file served from
/// <c>/.well-known/acme-challenge/</c>, and it needs no credentials for anyone's DNS provider.
/// The one thing it cannot do is wildcards, which is why the managed-zone wildcard is a
/// separate DNS-01 piece of work rather than something this grows into.</para>
/// </summary>
public sealed class CertesAcmeIssuer(
    IServiceProvider services,
    AcmeChallengeStore challenges,
    IOptions<CertificateOptions> options,
    TimeProvider clock,
    ILogger<CertesAcmeIssuer> logger) : IAcmeIssuer
{
    /// <summary>
    /// Serialises account creation. Two concurrent first-issuances would otherwise each find no
    /// account, each register one, and each write a row — leaving the platform with two ACME
    /// identities and its issuance history split across both, which is precisely what the CA's
    /// rate limits are counted against.
    /// </summary>
    private readonly SemaphoreSlim accountGate = new(1, 1);

    /// <summary>Resolved per call for the same reason as in <see cref="CertificateStore"/>.</summary>
    private ITransitEncryptor Transit => services.GetRequiredService<ITransitEncryptor>();

    private IAcmeContext? cachedAcme;

    public async Task<IssuedCertificate> IssueAsync(string hostname, CancellationToken ct)
    {
        var normalized = CertificateStore.Normalize(hostname);
        var config = options.Value;
        var acme = await GetAcmeContextAsync(config, ct);

        logger.LogInformation("Requesting a certificate for {Hostname} from {Directory}.",
            normalized, config.AcmeDirectory);

        var order = await acme.NewOrder([normalized]);
        var authorizations = await order.Authorizations();

        foreach (var authorization in authorizations)
        {
            var challenge = await authorization.Http();

            // Published before Validate, never after: the CA fetches the token as part of
            // handling that call, and a race here reads as a validation failure with no
            // explanation on either side.
            await challenges.PublishAsync(challenge.Token, challenge.KeyAuthz, ct);
            try
            {
                await challenge.Validate();
                await WaitForAuthorizationAsync(authorization, normalized, ct);
            }
            finally
            {
                // Dropped whether or not it worked. A key authorization left in Redis is a
                // small standing risk for no benefit once the order has moved on.
                await challenges.RemoveAsync(challenge.Token, CancellationToken.None);
            }
        }

        // A key per certificate, generated here and never reused. Sharing one across hostnames
        // would make a single disclosure impersonate every tenant at once.
        var certificateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var chain = await order.Generate(new CsrInfo { CommonName = normalized }, certificateKey);

        logger.LogInformation("Certificate issued for {Hostname}.", normalized);
        return new IssuedCertificate(chain.ToPem(), certificateKey.ToPem());
    }

    /// <summary>
    /// Polls the authorization until the CA has made up its mind. An invalid authorization
    /// carries the CA's own explanation of what it saw, and that sentence is worth far more to
    /// whoever configured the DNS than "issuance failed" — so it is lifted into the exception
    /// message and ends up on the domain row in the admin UI.
    /// </summary>
    private async Task WaitForAuthorizationAsync(IAuthorizationContext authorization, string hostname, CancellationToken ct)
    {
        var deadline = clock.GetUtcNow().AddMinutes(2);
        while (true)
        {
            var resource = await authorization.Resource();
            switch (resource.Status)
            {
                case AuthorizationStatus.Valid:
                    return;
                case AuthorizationStatus.Pending:
                    break;
                default:
                    var detail = resource.Challenges?
                        .FirstOrDefault(c => c.Error is not null)?.Error?.Detail;
                    throw new AcmeIssuanceException(
                        $"The certificate authority could not validate {hostname}: {resource.Status}"
                        + (detail is null ? "." : $" — {detail}"));
            }

            if (clock.GetUtcNow() > deadline)
            {
                throw new AcmeIssuanceException(
                    $"The certificate authority did not validate {hostname} within two minutes.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task<IAcmeContext> GetAcmeContextAsync(CertificateOptions config, CancellationToken ct)
    {
        if (cachedAcme is not null)
        {
            return cachedAcme;
        }

        await accountGate.WaitAsync(ct);
        try
        {
            if (cachedAcme is not null)
            {
                return cachedAcme;
            }

            var directoryUri = new Uri(config.AcmeDirectory);
            var http = BuildHttpClient(config, directoryUri);

            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
            var row = await db.AcmeAccounts.FirstOrDefaultAsync(a => a.DirectoryUrl == config.AcmeDirectory, ct);

            if (row is not null)
            {
                var pem = Encoding.UTF8.GetString(await Transit.DecryptAsync(
                    VaultTransitServiceCollectionExtensions.TlsKeysKey, row.EncryptedAccountKey, ct));
                return cachedAcme = new AcmeContext(directoryUri, KeyFactory.FromPem(pem), http);
            }

            if (string.IsNullOrWhiteSpace(config.ContactEmail))
            {
                throw new InvalidOperationException(
                    "Edge:Certificates:ContactEmail is required to register an ACME account. The CA "
                    + "sends expiry and policy notices there, and refuses registration without it.");
            }

            var accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
            var acme = new AcmeContext(directoryUri, accountKey, http);
            var account = await acme.NewAccount(config.ContactEmail, termsOfServiceAgreed: true);

            db.AcmeAccounts.Add(new AcmeAccount
            {
                DirectoryUrl = config.AcmeDirectory,
                ContactEmail = config.ContactEmail,
                EncryptedAccountKey = await Transit.EncryptAsync(
                    VaultTransitServiceCollectionExtensions.TlsKeysKey,
                    Encoding.UTF8.GetBytes(accountKey.ToPem()),
                    ct),
                AccountUrl = account.Location?.ToString(),
            });
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Registered a new ACME account with {Directory}.", config.AcmeDirectory);
            return cachedAcme = acme;
        }
        finally
        {
            accountGate.Release();
        }
    }

    private static IAcmeHttpClient BuildHttpClient(CertificateOptions config, Uri directoryUri)
    {
        if (!config.AcceptInsecureAcmeDirectory)
        {
            return new AcmeHttpClient(directoryUri, new HttpClient());
        }

        // Refused for a real CA rather than merely discouraged. This switch exists so the
        // compose `acmetest` profile can drive a throwaway Pebble server, which serves a
        // self-signed certificate by design; pointed at Let's Encrypt it would turn issuance
        // into something an on-path attacker could substitute certificates into.
        if (directoryUri.Host.EndsWith("letsencrypt.org", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Edge:Certificates:AcceptInsecureAcmeDirectory cannot be used with Let's Encrypt. "
                + "It exists for the local Pebble test CA only.");
        }

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        return new AcmeHttpClient(directoryUri, new HttpClient(handler));
    }
}

/// <summary>
/// Issuance failed for a reason worth showing an operator, as opposed to a bug. The message is
/// written to <c>edge.certificates.last_error</c> and surfaced next to the domain.
/// </summary>
public sealed class AcmeIssuanceException(string message) : Exception(message);
