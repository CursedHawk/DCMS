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
    /// Whether Kestrel opens the TLS listener at all. Off in dev, where nothing owns a public
    /// name to be issued a certificate for; on everywhere the edge is the ingress.
    /// </summary>
    public bool TlsEnabled { get; set; }

    public int HttpsPort { get; set; } = 8443;

    /// <summary>
    /// The plain-HTTP port. It carries two things and only two: the ACME HTTP-01 challenge,
    /// which is plain HTTP by definition, and a redirect to HTTPS for everything else.
    ///
    /// <para>Bound by the same configurator as the HTTPS port rather than left to
    /// ASPNETCORE_URLS, because an explicit <c>Listen</c> call makes Kestrel ignore that
    /// variable entirely — configuring one port here and expecting the environment to supply
    /// the other silently loses the other.</para>
    /// </summary>
    public int HttpPort { get; set; } = 8080;

    /// <summary>
    /// The port the outside world reaches HTTPS on, used only to build redirect URLs.
    ///
    /// <para>Separate from <see cref="HttpsPort"/> because the container listens on an
    /// <b>unprivileged</b> port and Docker publishes 443 to it. It has to: the image runs as
    /// <c>$APP_UID</c>, and a non-root process cannot bind 443. Publishing <c>443:8443</c> is
    /// what keeps the edge unprivileged.
    ///
    /// <para>The consequence is that the listener port is the wrong number to put in a redirect.
    /// Emitting <c>https://host:8443/</c> to a browser sends it somewhere nothing is published,
    /// and the failure looks like the site being down rather than like a misconfigured port.</para>
    /// </summary>
    public int PublicHttpsPort { get; set; } = 443;

    /// <summary>
    /// HSTS max-age in seconds, or 0 for no header.
    ///
    /// <para>Off by default, and that is a decision rather than an omission. HSTS is a promise a
    /// browser remembers for as long as it says, on a domain the tenant owns and we merely
    /// serve; setting one on their behalf is not ours to do by default, and getting out of it
    /// takes as long as the max-age. Phase 5 makes it a per-domain setting the tenant chooses.</para>
    /// </summary>
    public int HstsMaxAgeSeconds { get; set; }

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
    /// is a verified domain linked to a site. Asked over HTTP rather than read from tenancy
    /// directly, because querying it would hand the most exposed process on the platform a
    /// second schema it does not otherwise need.
    ///
    /// <para>Its sibling <c>/internal/tls-hostnames</c>, which the renewal sweep uses to find
    /// the hostnames it holds no certificate for, is derived from this address rather than
    /// configured separately — see <c>TlsAllowList.AllowedHostnamesAsync</c>.</para>
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
