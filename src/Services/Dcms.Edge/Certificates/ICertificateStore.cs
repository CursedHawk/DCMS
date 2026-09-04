using System.Net.Security;
using Dcms.Shared.Data.Edge;

namespace Dcms.Edge.Certificates;

/// <summary>
/// The certificate a TLS handshake is served from, and the record of how it got there.
///
/// <para>An interface so <see cref="CertificateProvisioner"/>'s rate-limit guards can be
/// asserted without a database — they are the guards standing between this platform and a
/// week-long CA block, so "probably correct" is not good enough for them.</para>
/// </summary>
public interface ICertificateStore
{
    Task<SslStreamCertificateContext?> GetAsync(string hostname, CancellationToken ct);

    Task SaveAsync(string hostname, string pemChain, string pemPrivateKey, CertificateSource source, CancellationToken ct);

    Task RecordFailureAsync(string hostname, string error, CancellationToken ct);

    /// <summary>When this hostname may next be attempted, or null if it may be attempted now.</summary>
    Task<DateTimeOffset?> RetryNotBeforeAsync(string hostname, CertificateOptions options, CancellationToken ct);

    void Invalidate(string hostname);

    /// <summary>
    /// Whether an operator has asked for this hostname to be reissued before it is due. Both the
    /// event-driven path and the hourly sweep consult it, so a dropped message costs an hour
    /// rather than the reissue.
    /// </summary>
    Task<bool> IsReissueRequestedAsync(string hostname, CancellationToken ct);
}

/// <summary>Whether a hostname may be issued a certificate at all. See <see cref="TlsAllowList"/>.</summary>
public interface ITlsAllowList
{
    Task<bool> IsAllowedAsync(string hostname, CancellationToken ct);

    /// <summary>
    /// Every tenant hostname that may hold a certificate right now, so the edge can issue ahead
    /// of the first visitor instead of inside their handshake. Empty when site-host cannot be
    /// reached — the caller treats that as "nothing new to do", never as "nothing is allowed".
    /// </summary>
    Task<IReadOnlyList<string>> AllowedHostnamesAsync(CancellationToken ct);
}
