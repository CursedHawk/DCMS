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

/// <summary>
/// Issuance failed before the certificate authority was ever asked — the account key could not
/// be decrypted, Vault was unreachable, the directory was misconfigured.
///
/// <para>Its own type because the failure backoff must not be charged for it. The backoff
/// doubles per failure and reaches hours, and it exists to protect the ACME account's rate
/// limit from a hostname whose authorization keeps failing. A hostname the CA never heard about
/// has not consumed anything, and recording one is how a ten-minute outage of ours turns into
/// an afternoon of the platform refusing to retry: on 2026-09-05 every provisioned tenant site
/// carried between one and five of these, recorded against Vault handing back an expired
/// token.</para>
///
/// <para>Callers count it as a failure, log it, and leave the hostname immediately retryable.
/// </para>
/// </summary>
public sealed class CertificateIssuanceUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
