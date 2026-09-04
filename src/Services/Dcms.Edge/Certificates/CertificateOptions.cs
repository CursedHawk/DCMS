namespace Dcms.Edge.Certificates;

/// <summary>
/// How the edge obtains and serves TLS certificates. Bound from <c>Edge:Certificates</c>.
/// </summary>
public sealed class CertificateOptions
{
    public const string SectionName = "Edge:Certificates";

    /// <summary>Let's Encrypt's production directory. Real certificates, real rate limits.</summary>
    public const string LetsEncryptProduction = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>Let's Encrypt's staging directory. Untrusted certificates, generous limits.</summary>
    public const string LetsEncryptStaging = "https://acme-staging-v02.api.letsencrypt.org/directory";

    /// <summary>
    /// Whether Kestrel opens the TLS listener at all. Off until the cutover, so the edge can run
    /// alongside Caddy without either of them claiming the same port.
    /// </summary>
    public bool TlsEnabled { get; set; }

    public int HttpsPort { get; set; } = 8443;

    /// <summary>
    /// <b>Staging by default, and that is the safety property, not a placeholder.</b> Let's
    /// Encrypt allows roughly 50 certificates per registered domain per week and counts failed
    /// authorizations too. A dev deployment that inherited production's directory by default
    /// would spend production's budget on throwaway hostnames, and the symptom is every real
    /// tenant's issuance failing at once. Production sets this explicitly.
    /// </summary>
    public string AcmeDirectory { get; set; } = LetsEncryptStaging;

    /// <summary>The ACME account contact. Where the CA sends expiry warnings.</summary>
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>Renew once this much life is left. 30 days is the usual margin on a 90-day certificate.</summary>
    public int RenewBeforeDays { get; set; } = 30;

    /// <summary>How often the renewal sweep runs. Cheap: one indexed query over a small table.</summary>
    public int RenewalSweepMinutes { get; set; } = 60;

    /// <summary>
    /// Issue during the TLS handshake for a hostname with no certificate yet. This is the
    /// fallback, not the main path — <see cref="DomainCertificateProvisioner"/> pre-issues when
    /// a domain is verified, so by the time a visitor arrives the certificate normally exists.
    /// </summary>
    public bool AllowOnDemand { get; set; } = true;

    /// <summary>
    /// How long a handshake may block waiting for an on-demand issuance. Past this the
    /// connection is refused rather than left hanging: a browser showing an error is better
    /// than a browser showing nothing.
    /// </summary>
    public int OnDemandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// The authorization gate for issuing at all: site-host answers 200 only for a hostname that
    /// is a verified domain linked to a site. Reused rather than reimplemented — it is the same
    /// endpoint Caddy's <c>on_demand_tls ask</c> calls today, and querying tenancy directly would
    /// hand the most exposed process on the platform a second schema it does not otherwise need.
    /// </summary>
    public string TlsAllowedEndpoint { get; set; } = "http://site-host:8080/internal/tls-allowed";

    /// <summary>
    /// Back off this long after a failed attempt, doubling per consecutive failure and capped at
    /// a day. A hostname whose DNS never resolves must not be able to retry in a loop: the CA
    /// rate-limits failed authorizations, so one broken domain can exhaust every other tenant's
    /// issuance budget.
    /// </summary>
    public int FailureBackoffSeconds { get; set; } = 300;

    /// <summary>
    /// Trust the ACME directory's TLS certificate without validating it. <b>For Pebble only</b>
    /// — the throwaway ACME server the compose <c>acmetest</c> profile runs, which serves a
    /// self-signed certificate by design. Refused when the directory is a Let's Encrypt one.
    /// </summary>
    public bool AcceptInsecureAcmeDirectory { get; set; }
}
