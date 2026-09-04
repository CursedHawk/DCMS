using Dcms.Shared.Data.Edge;

namespace Dcms.Edge.Certificates;

/// <summary>A certificate and its private key, both PEM-encoded, as issued.</summary>
public sealed record IssuedCertificate(string PemChain, string PemPrivateKey);

/// <summary>
/// Obtains a certificate for one hostname from a certificate authority.
///
/// <para>An interface because the concrete implementation is Certes, which is a small and
/// lightly-maintained library. The surface used is narrow — new order, HTTP-01 authorization,
/// finalize — so swapping it is a day's work as long as nothing else depends on its types.
/// Also what makes the issuance path testable without an ACME server.</para>
/// </summary>
public interface IAcmeIssuer
{
    Task<IssuedCertificate> IssueAsync(string hostname, CancellationToken ct);
}
