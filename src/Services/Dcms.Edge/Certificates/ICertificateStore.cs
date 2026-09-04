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
}

/// <summary>Whether a hostname may be issued a certificate at all. See <see cref="TlsAllowList"/>.</summary>
public interface ITlsAllowList
{
    Task<bool> IsAllowedAsync(string hostname, CancellationToken ct);
}
