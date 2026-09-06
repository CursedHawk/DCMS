using System.Text;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Dcms.Edge.Certificates.Dns;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates;

/// <summary>
/// Obtains certificates from an ACME certificate authority, over HTTP-01 or DNS-01 depending on
/// what the order contains.
///
/// <para><b>HTTP-01 is still the default, and stays the path for a tenant's own domain.</b> The
/// edge already owns port 80 (it has to, for the HTTPS redirect), the challenge is a plain file
/// served from <c>/.well-known/acme-challenge/</c>, and it needs no credentials for anybody's
/// DNS provider — so a tenant pointing a domain at us needs nothing from us but a CNAME.</para>
///
/// <para><b>DNS-01 is used when, and only when, the order contains a wildcard.</b> Let's Encrypt
/// refuses every other challenge type for a wildcard identifier, and a wildcard is the only
/// thing that removes this platform's per-registered-domain issuance ceiling. It costs a
/// credential with DNS-edit rights on the zone, which is why it is scoped to the domains DCMS
/// owns rather than offered for tenant domains. See ADR 0011.</para>
/// </summary>
public sealed class CertesAcmeIssuer(
    IServiceProvider services,
    AcmeChallengeStore challenges,
    IDnsChallengeWriter dns,
    DnsPropagationWaiter propagation,
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

    public Task<IssuedCertificate> IssueAsync(string hostname, CancellationToken ct)
        => IssueAsync([hostname], ct);

    public async Task<IssuedCertificate> IssueAsync(IReadOnlyList<string> identifiers, CancellationToken ct)
    {
        if (identifiers.Count == 0)
        {
            throw new ArgumentException("A certificate needs at least one identifier.", nameof(identifiers));
        }

        var normalized = identifiers.Select(CertificateStore.Normalize).Distinct(StringComparer.Ordinal).ToList();
        var config = options.Value;
        var acme = await GetAcmeContextAsync(config, ct);

        // One wildcard puts the WHOLE order on DNS-01. Not a preference: Let's Encrypt refuses
        // HTTP-01 and TLS-ALPN-01 for a wildcard identifier, and an order is validated as a
        // unit, so a mixed order still needs every authorization answered the same way.
        var useDns = normalized.Any(DnsChallenge.IsWildcard);

        logger.LogInformation(
            "Requesting a certificate for {Identifiers} from {Directory} over {Challenge}.",
            string.Join(", ", normalized), config.AcmeDirectory, useDns ? "DNS-01" : "HTTP-01");

        var order = await acme.NewOrder(normalized);
        var authorizations = await order.Authorizations();

        if (useDns)
        {
            await ValidateOverDnsAsync(acme, authorizations, ct);
        }
        else
        {
            await ValidateOverHttpAsync(authorizations, ct);
        }

        // A key per certificate, generated here and never reused. Sharing one across hostnames
        // would make a single disclosure impersonate every tenant at once.
        var certificateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);

        // The first identifier becomes the CN. Which one hardly matters to a modern client --
        // validation is done against the SAN list -- but it must be one of them, and it must be
        // <=64 characters or the CA rejects the CSR.
        var commonName = normalized.FirstOrDefault(n => n.Length <= 64) ?? normalized[0];
        var chain = await order.Generate(new CsrInfo { CommonName = commonName }, certificateKey);

        logger.LogInformation("Certificate issued for {Identifiers}.", string.Join(", ", normalized));
        return new IssuedCertificate(chain.ToPem(), certificateKey.ToPem());
    }

    /// <summary>The original path, unchanged: a token served from Redis over plain HTTP.</summary>
    private async Task ValidateOverHttpAsync(IEnumerable<IAuthorizationContext> authorizations, CancellationToken ct)
    {
        foreach (var authorization in authorizations)
        {
            var challenge = await authorization.Http();
            var identifier = (await authorization.Resource()).Identifier.Value;

            // Published before Validate, never after: the CA fetches the token as part of
            // handling that call, and a race here reads as a validation failure with no
            // explanation on either side.
            await challenges.PublishAsync(challenge.Token, challenge.KeyAuthz, ct);
            try
            {
                await challenge.Validate();
                await WaitForAuthorizationAsync(authorization, identifier, ct);
            }
            finally
            {
                // Dropped whether or not it worked. A key authorization left in Redis is a
                // small standing risk for no benefit once the order has moved on.
                await challenges.RemoveAsync(challenge.Token, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Publishes every challenge record for the order, waits for the lot to go live, and only
    /// then asks the CA to validate.
    ///
    /// <para><b>All records first, then all validations.</b> This ordering is the whole reason
    /// this method is not a loop like the HTTP one. An order for <c>highgeek.eu</c> and
    /// <c>*.highgeek.eu</c> produces two authorizations whose challenge records share one name —
    /// ACME strips the wildcard label, so both are <c>_acme-challenge.highgeek.eu</c> — carrying
    /// two different values that must be resolvable simultaneously. Validating the first
    /// authorization before publishing the second is fine; publishing the second by
    /// <i>replacing</i> the first is not, and neither is removing the first record in a
    /// per-authorization <c>finally</c> while the second is still to be checked. Doing the whole
    /// order in one pass makes both mistakes unavailable.</para>
    /// </summary>
    private async Task ValidateOverDnsAsync(
        IAcmeContext acme, IEnumerable<IAuthorizationContext> authorizations, CancellationToken ct)
    {
        var published = new List<DnsChallengeRecord>();
        var pending = new List<(IAuthorizationContext Authorization, IChallengeContext Challenge,
            string Identifier, string RecordName, string Value)>();

        try
        {
            foreach (var authorization in authorizations)
            {
                var resource = await authorization.Resource();

                // The CA reports a wildcard authorization with the base domain in Identifier
                // and a Wildcard flag, so this value is already the name the record belongs at.
                var identifier = resource.Identifier.Value;
                var challenge = await authorization.Dns()
                                ?? throw new AcmeIssuanceException(
                                    $"The certificate authority offered no DNS-01 challenge for {identifier}, "
                                    + "so a wildcard cannot be issued from this directory.");

                var value = acme.AccountKey.DnsTxt(challenge.Token);
                var recordName = $"{DnsChallenge.Prefix}.{identifier}";

                published.Add(await dns.AddTxtAsync(recordName, value, ct));
                pending.Add((authorization, challenge, identifier, recordName, value));
            }

            // Grouped by record name, asserting every value at that name at once -- which is
            // exactly the apex-plus-wildcard case. Waiting per authorization would let the first
            // pass as soon as its own value appeared, while the second was still propagating.
            foreach (var group in pending.GroupBy(p => p.RecordName, StringComparer.Ordinal))
            {
                await propagation.WaitAsync(
                    group.Key, [.. group.Select(p => p.Value)], ct);
            }

            foreach (var item in pending)
            {
                await item.Challenge.Validate();
                await WaitForAuthorizationAsync(item.Authorization, item.Identifier, ct);
            }
        }
        finally
        {
            // Always, and never allowed to throw -- RemoveAsync swallows its own failures. A
            // certificate that was issued must not be lost because tidying up afterwards failed.
            foreach (var record in published)
            {
                await dns.RemoveAsync(record, CancellationToken.None);
            }
        }
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
                // Wrapped, because this is the one step in issuance that fails without the CA
                // ever hearing about it, and the caller's backoff must not be charged for it.
                // See CertificateIssuanceUnavailableException.
                string pem;
                try
                {
                    pem = Encoding.UTF8.GetString(await Transit.DecryptAsync(
                        VaultTransitServiceCollectionExtensions.TlsKeysKey, row.EncryptedAccountKey, ct));
                }
                catch (Exception ex)
                {
                    throw new CertificateIssuanceUnavailableException(
                        "The ACME account key could not be decrypted, so no certificate can be "
                        + "ordered. Check Vault Transit and the edge's AppRole.", ex);
                }

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

            // Encrypted BEFORE the account is registered. The other order registers an ACME
            // identity with the CA and then discovers it cannot store the key that owns it --
            // an account nothing can ever use again, and a second one registered on the next
            // attempt, splitting the rate limit this platform is counted under across both.
            string encryptedAccountKey;
            try
            {
                encryptedAccountKey = await Transit.EncryptAsync(
                    VaultTransitServiceCollectionExtensions.TlsKeysKey,
                    Encoding.UTF8.GetBytes(accountKey.ToPem()),
                    ct);
            }
            catch (Exception ex)
            {
                throw new CertificateIssuanceUnavailableException(
                    "A new ACME account key could not be encrypted, so registering one would "
                    + "strand it. Check Vault Transit and the edge's AppRole.", ex);
            }

            var account = await acme.NewAccount(config.ContactEmail, termsOfServiceAgreed: true);

            db.AcmeAccounts.Add(new AcmeAccount
            {
                DirectoryUrl = config.AcmeDirectory,
                ContactEmail = config.ContactEmail,
                EncryptedAccountKey = encryptedAccountKey,
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
