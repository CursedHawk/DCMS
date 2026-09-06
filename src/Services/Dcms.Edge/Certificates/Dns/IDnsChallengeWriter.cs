namespace Dcms.Edge.Certificates.Dns;

/// <summary>
/// A TXT record this process created for a DNS-01 challenge, and everything needed to take it
/// away again. Opaque to the caller: only the writer that made it knows how to remove it.
/// </summary>
/// <param name="ZoneId">The provider's id for the zone the record lives in.</param>
/// <param name="RecordId">The provider's id for the record itself.</param>
/// <param name="Name">The fully-qualified record name, for logging.</param>
public sealed record DnsChallengeRecord(string ZoneId, string RecordId, string Name);

/// <summary>
/// Publishes and retracts the <c>_acme-challenge</c> TXT records a DNS-01 order needs.
///
/// <para><b>Why DNS-01 exists here at all.</b> Let's Encrypt will not validate a wildcard
/// identifier over HTTP-01 or TLS-ALPN-01 — DNS-01 is the only option — and a wildcard is the
/// only thing that removes this platform's issuance ceiling. See ADR 0011.</para>
///
/// <para><b>Add, never replace.</b> An order for <c>highgeek.eu</c> and <c>*.highgeek.eu</c>
/// produces two authorizations that both publish at <c>_acme-challenge.highgeek.eu</c>, with
/// different values, and both must be resolvable at the same time. A writer that "sets" the
/// record satisfies the second authorization and fails the first. This is the single most
/// common way a DNS-01 implementation is wrong, and it only shows up on the apex-plus-wildcard
/// combination — which is exactly ours.</para>
///
/// <para>An interface so the ACME integration test can drive Pebble's challenge test server
/// instead of a real DNS provider, and so a second provider is a class rather than a
/// refactor.</para>
/// </summary>
public interface IDnsChallengeWriter
{
    /// <summary>
    /// Publishes one TXT value at <paramref name="name"/> without disturbing any other value
    /// already there.
    /// </summary>
    Task<DnsChallengeRecord> AddTxtAsync(string name, string value, CancellationToken ct);

    /// <summary>
    /// Removes a record this writer created. Called in a <c>finally</c>, so it must never throw:
    /// a cleanup failure is worth a log line and nothing more, because the alternative is losing
    /// a certificate that was successfully issued.
    /// </summary>
    Task RemoveAsync(DnsChallengeRecord record, CancellationToken ct);

    /// <summary>
    /// Whether this writer can publish for <paramref name="identifier"/> — for Cloudflare,
    /// whether the zone is in our account.
    ///
    /// <para>Asked by the console before saving a managed certificate, so "we have no DNS
    /// control over that domain" is a validation message at the moment somebody types it, rather
    /// than an ACME failure discovered an hour later by whoever reads the logs.</para>
    /// </summary>
    Task<bool> CanPublishForAsync(string identifier, CancellationToken ct);
}

/// <summary>
/// The DNS name a challenge for <paramref name="identifier"/> is published at.
/// </summary>
public static class DnsChallenge
{
    public const string Prefix = "_acme-challenge";

    /// <summary>
    /// <c>*.dcms.highgeek.eu</c> and <c>dcms.highgeek.eu</c> both validate at
    /// <c>_acme-challenge.dcms.highgeek.eu</c> — the wildcard label is stripped, not encoded.
    /// That collision is deliberate in the protocol and is why records must be added rather than
    /// replaced.
    /// </summary>
    public static string RecordName(string identifier)
        => $"{Prefix}.{BaseDomain(identifier)}";

    /// <summary>The identifier with any leading wildcard label removed.</summary>
    public static string BaseDomain(string identifier)
        => identifier.StartsWith("*.", StringComparison.Ordinal) ? identifier[2..] : identifier;

    /// <summary>Whether this identifier forces the whole order onto DNS-01.</summary>
    public static bool IsWildcard(string identifier)
        => identifier.StartsWith("*.", StringComparison.Ordinal);
}
