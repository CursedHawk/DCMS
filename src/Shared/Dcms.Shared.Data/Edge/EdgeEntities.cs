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

    /// <summary>
    /// Lowercase, no port. The SNI name the handshake presents, and the row's identity for
    /// every per-hostname certificate.
    ///
    /// <para>For a managed certificate covering several names it is the first identifier, which
    /// makes it a label rather than the whole truth — <see cref="SubjectAlternativeNames"/> is
    /// what the handshake matches against in that case. The row is still found by
    /// <see cref="ManagedCertificateId"/> first, so reordering the identifiers renames this
    /// column instead of creating a second row.</para>
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// Every name the leaf actually covers, read off its SAN extension when it is stored.
    ///
    /// <para>Derived rather than declared, so it cannot disagree with the certificate being
    /// served. It exists because a certificate stopped being one hostname: the handshake looks
    /// up an exact <see cref="Hostname"/> first and falls back to matching this, which is how
    /// <c>anything.dcms.highgeek.eu</c> finds the row holding <c>*.dcms.highgeek.eu</c>.</para>
    /// </summary>
    public string[] SubjectAlternativeNames { get; set; } = [];

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

    /// <summary>
    /// The <see cref="EdgeManagedCertificate"/> this artifact was ordered for, or null for a
    /// per-hostname certificate — a tenant's own domain issued over HTTP-01, or one they
    /// uploaded.
    ///
    /// <para>Unique where set: one intent produces one certificate. It is also the key the
    /// renewal path looks the row up by, which is what keeps editing a managed certificate's
    /// identifiers from stranding the old row and ordering a second one.</para>
    /// </summary>
    public Guid? ManagedCertificateId { get; set; }

    public DateTimeOffset? RenewedAt { get; set; }

    /// <summary>
    /// Set when an operator asks for a fresh certificate before this one is due.
    ///
    /// <para>A durable request rather than a message, because that is the difference between a
    /// button that works and one that usually works: admin-api writes this and publishes an
    /// event to make the edge act now, and the hourly renewal sweep also picks up anything
    /// flagged — so a dropped message costs an hour, not the reissue. Cleared by
    /// <c>CertificateStore.SaveAsync</c>, which is the only thing that can honestly say it is
    /// done.</para>
    /// </summary>
    public DateTimeOffset? ReissueRequestedAt { get; set; }

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
/// a bind-mounted config file had.</para>
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

/// <summary>
/// A certificate the platform keeps renewed on its own behalf, described by the names it must
/// cover rather than by a single hostname.
///
/// <para><b>Why this is a table and not configuration.</b> The platform's own hostnames were
/// compiled into <c>EdgeOptions.PlatformHostnames</c>, so keeping a new domain renewed meant a
/// code change and a deploy. Rows here are editable by a superadmin from the platform console,
/// which is the difference between "we support that" and "we support that next release".</para>
///
/// <para><b>Why identifiers rather than one hostname.</b> A wildcard is the only thing that
/// removes the ceiling this platform was built into: Let's Encrypt counts 50 certificates per
/// registered domain per week, and every name DCMS serves is under one registered domain, so
/// per-hostname issuance caps the platform at ~50 new tenants a week and then fails for
/// everyone. One certificate for <c>highgeek.eu</c>, <c>*.highgeek.eu</c> and
/// <c>*.dcms.highgeek.eu</c> covers every hostname vps1 serves. Three identifiers and not two,
/// because a wildcard matches exactly one label: it covers neither the apex nor a deeper
/// subdomain.</para>
///
/// <para>Any identifier beginning <c>*.</c> forces the whole order onto DNS-01 — Let's Encrypt
/// refuses HTTP-01 and TLS-ALPN-01 for a wildcard. See
/// <c>docs/adr/0011-wildcard-tls-dns01.md</c>.</para>
///
/// <para><b>There is deliberately no TenantId</b>, for the reason recorded on
/// <see cref="EdgeCertificate"/>: these are the platform's own domains, and a tenant's
/// relationship to a hostname lives in <c>tenancy.domains</c>. If that ever changes, this table
/// must be registered in <c>RlsConfigurator.TenantTables</c> and in <c>AssertRlsCoverage</c> in
/// the same commit.</para>
/// </summary>
public sealed class EdgeManagedCertificate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What an operator calls it in the console. Not used for issuance.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The ACME identifiers to order, wildcards allowed. The first is used as the artifact
    /// row's <see cref="EdgeCertificate.Hostname"/> label.
    /// </summary>
    public string[] Identifiers { get; set; } = [];

    /// <summary>
    /// Disabled rows are neither ordered nor renewed, and their existing certificate keeps
    /// serving until it expires. That is the rollback for this whole feature: one column.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Set when an operator presses "renew now", cleared once the CA has answered.
    ///
    /// <para><b>Here and not on <see cref="EdgeCertificate.ReissueRequestedAt"/></b>, which is
    /// where it started. That flag lives on the issued artifact, so a managed certificate that
    /// has never been issued — the state every one of them is in until the first order succeeds,
    /// and the state the platform wildcard was in on the day this was written — had nowhere to
    /// record the request. The endpoint set nothing, the console showed nothing, and the button
    /// reported success while leaving no trace: the exact "nothing happens" it was built to
    /// avoid. The request belongs to the intent, which always exists.</para>
    ///
    /// <para>Cleared only when the CA has actually answered, so a request survives a missing
    /// Cloudflare token and is honoured the moment one is written. See
    /// <c>ManagedCertificateProvisioner.SweepAsync</c>.</para>
    /// </summary>
    public DateTimeOffset? ReissueRequestedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One attempt to order a managed certificate, successful or not.
///
/// <para><b>This is a rate-limit guard first and a history second.</b> Let's Encrypt allows five
/// certificates per identical set of identifiers per seven days, and counts renewals against it.
/// A per-hostname model spread that risk across hostnames; one certificate covering the whole
/// platform concentrates it, so a retry loop can lock every hostname out at once for a week.
/// </para>
///
/// <para>It is kept here rather than in <see cref="EdgeCertificate.ConsecutiveFailures"/>
/// precisely because that counter is <i>cleared on every restart</i> by the edge's TLS preflight
/// — correct for a hostname whose DNS an operator has just fixed, and catastrophic for a weekly
/// budget, since a crash-looping container would spend it in minutes. Nothing clears these rows;
/// they age out.</para>
/// </summary>
public sealed class EdgeManagedCertificateAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ManagedCertificateId { get; set; }

    /// <summary>
    /// The identifier set as ordered, joined by spaces. Recorded per attempt rather than read
    /// from the parent row, because the CA's limit is counted against the exact set that was
    /// ordered — editing the identifiers starts a new budget, and this is what says so.
    /// </summary>
    public string Identifiers { get; set; } = string.Empty;

    public DateTimeOffset AttemptedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Succeeded { get; set; }

    /// <summary>
    /// Whether the CA was actually asked. False for an attempt that failed before any order was
    /// placed — no Cloudflare token, Vault unreachable, no zone for the name.
    ///
    /// <para><b>It is what lets a failure be visible without being charged.</b> These attempts
    /// were originally not recorded at all, on the correct reasoning that nothing was spent — and
    /// the result was a console that showed a certificate with no expiry, no error and no
    /// history, and a "renew now" button that reported success and changed nothing. The
    /// rate-limit ledger and the operator's answer are two different jobs; this column separates
    /// them, so the row appears in the history and is skipped by
    /// <c>ManagedCertificateGuard</c>.</para>
    /// </summary>
    public bool ReachedCa { get; set; } = true;

    /// <summary>The CA's own sentence when it refused. Usually the only thing that says why.</summary>
    public string? Error { get; set; }
}
