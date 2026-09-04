namespace Dcms.Shared.Data.Edge;

/// <summary>Where a certificate came from, which decides whether the platform renews it.</summary>
public enum CertificateSource
{
    /// <summary>Issued by the platform over ACME and renewed automatically.</summary>
    DcmsManaged = 0,

    /// <summary>Uploaded by a tenant. Never renewed here; expiry is reported, not fixed.</summary>
    Custom = 1,
}

/// <summary>
/// One TLS certificate, keyed by the hostname it serves. Read at TLS handshake time by the edge
/// and written by the ACME issuer, the renewal service, or a tenant uploading their own.
///
/// <para><b>There is deliberately no TenantId here.</b> A certificate belongs to a
/// <i>hostname</i>; which tenant may see or replace it is derived from <c>tenancy.domains</c>,
/// which already carries the ownership and is already tenant-filtered. Keeping the column off
/// this table is what keeps it legitimately outside <c>RlsConfigurator.TenantTables</c> rather
/// than missing from it — the distinction that mattered when <c>ai.user_ai_settings</c> was
/// granted by the schema-wide default and policed by nothing. If this table ever gains a
/// TenantId, it must be registered there and in <c>AssertRlsCoverage</c> in the same commit.</para>
/// </summary>
public sealed class EdgeCertificate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Lowercase, no port. The SNI name the handshake presents.</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Leaf followed by the issuing chain, PEM-encoded.</summary>
    public string PemChain { get; set; } = string.Empty;

    /// <summary>
    /// The private key, Vault-Transit encrypted. Never plaintext at rest: this is the one secret
    /// on the platform whose disclosure lets someone impersonate a tenant's site outright.
    /// </summary>
    public string EncryptedPrivateKey { get; set; } = string.Empty;

    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public string Issuer { get; set; } = string.Empty;
    public CertificateSource Source { get; set; } = CertificateSource.DcmsManaged;

    public DateTimeOffset? RenewedAt { get; set; }

    /// <summary>
    /// When issuance or renewal was last attempted, successful or not, and what went wrong.
    /// Surfaced in the admin UI: the whole point of moving certificates into the product is that
    /// "why has this domain no certificate" has an answer someone can read.
    /// </summary>
    public DateTimeOffset? LastAttemptAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// Drives the backoff that stops a hostname whose DNS never resolves from hammering the CA.
    /// Let's Encrypt rate-limits failed authorizations as well as issuance, so an unbounded
    /// retry loop on one broken domain can exhaust the budget for every other tenant.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsUsable(DateTimeOffset now) => PemChain.Length > 0 && NotAfter > now && NotBefore <= now;
}

/// <summary>
/// The platform's ACME account for one directory URL. Persisted because re-registering on every
/// start would mint a new account key each time, losing the issuance history the CA rate-limits
/// against and orphaning the authorizations already validated for our domains.
///
/// <para>One row per directory, so staging and production accounts coexist and a dev deployment
/// pointed at the staging directory can never spend production's rate limit.</para>
/// </summary>
public sealed class AcmeAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DirectoryUrl { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>The account key, Vault-Transit encrypted. Whoever holds it can revoke our certificates.</summary>
    public string EncryptedAccountKey { get; set; } = string.Empty;

    public string? AccountUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A route the edge serves in addition to the platform-plane table built from configuration.
///
/// <para>Rows here are an overlay, not a replacement: the four operator hosts stay in
/// configuration so the edge can serve them before Postgres answers, and this table carries what
/// only the running platform knows. A row is picked up on the next reload, which the domain
/// events already trigger — no restart, and none of the stale-inode problem the bind-mounted
/// Caddyfile had.</para>
/// </summary>
public sealed class EdgeRoute
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Unique across the whole table. YARP rejects a config with duplicate route ids.</summary>
    public string RouteId { get; set; } = string.Empty;

    /// <summary>Comma-separated hostnames. Empty matches any host, like the tenant catch-all.</summary>
    public string? Hosts { get; set; }

    /// <summary>An ASP.NET route template, e.g. <c>/api/{**catch-all}</c>.</summary>
    public string PathPattern { get; set; } = "/{**catch-all}";

    /// <summary>
    /// One of the cluster ids the static table defines. A row naming an unknown cluster is
    /// skipped with a warning rather than applied: YARP would reject the whole config, and one
    /// bad row must not be able to take every route down with it.
    /// </summary>
    public string ClusterId { get; set; } = string.Empty;

    /// <summary>Lower wins. Below 100 to out-rank the tenant catch-all; above it to fall behind.</summary>
    public int Order { get; set; } = 90;

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
