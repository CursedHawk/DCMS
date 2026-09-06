namespace Dcms.Edge.Certificates.Dns;

/// <summary>
/// How the edge publishes DNS-01 challenge records. Bound from <c>Edge:Dns</c>.
/// </summary>
public sealed class DnsOptions
{
    public const string SectionName = "Edge:Dns";

    public CloudflareOptions Cloudflare { get; set; } = new();

    /// <summary>
    /// How long to wait for a published TXT record to be visible on every authoritative
    /// nameserver for its zone before asking the CA to validate.
    ///
    /// <para>Two minutes is generous for Cloudflare, which is usually seconds. It is a ceiling
    /// on a wait, not a delay: the waiter returns as soon as the servers agree. Waiting costs
    /// one background minute; not waiting costs a failed authorization, and Let's Encrypt allows
    /// five of those per identifier per hour.</para>
    /// </summary>
    public int PropagationTimeoutSeconds { get; set; } = 120;

    /// <summary>How often to re-ask the authoritative servers while waiting.</summary>
    public int PropagationPollSeconds { get; set; } = 5;

    /// <summary>
    /// Management address of a <c>pebble-challtestsrv</c>, which replaces Cloudflare as the place
    /// challenge records are published. <b>For the local ACME test harness only.</b>
    ///
    /// <para>Empty everywhere except the compose <c>acmetest</c> profile, and it is refused
    /// outright unless <c>Edge:Certificates:AcceptInsecureAcmeDirectory</c> is also set — which
    /// is itself refused for a Let's Encrypt directory. Two switches rather than one because
    /// this one silently redirects where the platform proves domain control, and a deployment
    /// that had it on by accident would issue certificates whose validation nobody performed.
    /// </para>
    /// </summary>
    public string ChallTestSrvUrl { get; set; } = string.Empty;
}

/// <summary>
/// Cloudflare API credentials and behaviour.
///
/// <para>The token belongs in Vault at <c>secret/dcms/edge</c> as
/// <c>Edge__Dns__Cloudflare__ApiToken</c>; the edge's policy already grants read on that path,
/// so adding it needs no policy change.</para>
/// </summary>
public sealed class CloudflareOptions
{
    /// <summary>
    /// A <b>scoped</b> API token — <c>Zone:DNS:Edit</c> plus <c>Zone:Zone:Read</c>, restricted
    /// to the zones DCMS owns. Not a Global API Key, which carries the whole account and cannot
    /// be scoped down.
    ///
    /// <para>Empty disables DNS-01 entirely. That is a refusal, not a fallback: a wildcard order
    /// cannot be validated any other way, so the issuer raises
    /// <see cref="CertificateIssuanceUnavailableException"/> and the caller records a failure
    /// the CA was never asked about — costing no backoff and no rate limit.</para>
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    public string ApiBase { get; set; } = "https://api.cloudflare.com/client/v4";

    /// <summary>
    /// TTL for the challenge records. Deliberately the minimum Cloudflare accepts: the record
    /// lives for the length of one authorization and a long TTL only makes a stale value linger
    /// in resolvers after we have deleted it.
    /// </summary>
    public int RecordTtlSeconds { get; set; } = 60;
}
