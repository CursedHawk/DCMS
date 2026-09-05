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

    /// <summary>
    /// Clears the recorded failure on every hostname with issuance work outstanding — one that
    /// has never held a certificate, and one an operator has asked to reissue — and returns how
    /// many. Called once per process start, after the store has been proved working.
    ///
    /// <para><b>This is what makes fixing the cause enough.</b> The backoff doubles per failure
    /// and reaches hours, and it cannot tell a hostname the CA keeps refusing from one that
    /// failed because the platform's own store was broken. When Vault Transit was unreachable
    /// every platform hostname accumulated six failures, so repairing Vault would have been
    /// followed by an afternoon of the sweep skipping exactly the hostnames it had just become
    /// able to issue — with the operator watching a fixed platform stay dark.</para>
    ///
    /// <para>A pending reissue counts for the same reason and with the same evidence: an
    /// operator pressed the button, so somebody is watching for a certificate that a backoff
    /// they did not cause is holding back. Without this, one provisioned site sat on four
    /// failures earned entirely by an expired Vault token, with a reissue requested at 10:00
    /// that the sweep would not attempt for hours.</para>
    ///
    /// <para>Scoped to hostnames with work outstanding, and to process start rather than to
    /// every sweep, so it stays bounded: a domain whose DNS really is broken gets one extra
    /// attempt per restart, not a retry loop. A restart is the operator saying "I changed
    /// something, try again", and this is the edge taking them at their word.</para>
    /// </summary>
    Task<int> ClearBackoffForOutstandingWorkAsync(CancellationToken ct);

    /// <summary>
    /// Clears the recorded failure on every hostname that holds a usable certificate and is not
    /// due for renewal before <paramref name="renewalThreshold"/>, and returns how many. Called
    /// at the end of every sweep.
    ///
    /// <para>The companion to <see cref="ClearBackoffForOutstandingWorkAsync"/>, for the rows that
    /// one deliberately will not touch. A hostname that is serving TLS and is not due is not
    /// being ordered for at all, so the backoff recorded against it is holding back a request
    /// nobody is going to make — while still counting towards the <c>failing</c> gauge an alert
    /// fires on. That is how one afternoon's Vault outage stays on a dashboard indefinitely.
    /// </para>
    ///
    /// <para>Excludes anything inside the renewal window on purpose: there the backoff is live,
    /// and a certificate that is due and failing to renew is exactly what it is for.</para>
    /// </summary>
    Task<int> ClearBackoffForHealthyAsync(DateTimeOffset renewalThreshold, CancellationToken ct);
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
