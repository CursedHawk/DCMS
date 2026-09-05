using System.Collections.Concurrent;
using System.Net.Security;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Telemetry;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates;

/// <summary>
/// The one place a certificate is obtained: checks the hostname is ours, honours the failure
/// backoff, runs at most one issuance per hostname at a time, and stores the result.
///
/// <para>Every caller goes through here — the TLS handshake on a cache miss, the domain-verified
/// event, the renewal sweep, and an operator pressing "reissue". Concentrating it is what makes
/// the rate-limit protections real: a guard that three of four callers apply is not a guard.</para>
/// </summary>
public sealed class CertificateProvisioner(
    ICertificateStore store,
    IAcmeIssuer issuer,
    ITlsAllowList allowList,
    DcmsMetrics metrics,
    IOptions<CertificateOptions> options,
    ILogger<CertificateProvisioner> logger)
{
    /// <summary>
    /// One issuance per hostname at a time, per process. A popular domain whose certificate has
    /// just expired attracts many simultaneous handshakes, and without this each would start its
    /// own ACME order for the same name — burning the rate limit on duplicates of one certificate.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<SslStreamCertificateContext?>> inFlight = new();

    public Task<SslStreamCertificateContext?> EnsureAsync(string hostname, CancellationToken ct)
    {
        var normalized = CertificateStore.Normalize(hostname);
        return inFlight.GetOrAdd(normalized, key => RunAsync(key, ct))
            .ContinueWith(t =>
            {
                inFlight.TryRemove(normalized, out _);
                return t.GetAwaiter().GetResult();
            }, ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<SslStreamCertificateContext?> RunAsync(string hostname, CancellationToken ct)
    {
        var config = options.Value;

        // Re-read under the single-flight guard: while this request was queueing, the issuance
        // it was queueing behind may already have produced the certificate.
        //
        // Unless an operator asked for a new one. That is the whole of what the "reissue now"
        // button does -- without this check it would find the certificate it is trying to
        // replace, decide there was nothing to do, and report success.
        if (await store.GetAsync(hostname, ct) is { } existing
            && !await store.IsReissueRequestedAsync(hostname, ct))
        {
            return existing;
        }

        if (await store.RetryNotBeforeAsync(hostname, config, ct) is { } retryAt)
        {
            logger.LogDebug(
                "Skipping issuance for {Hostname}: backing off until {RetryAt:u} after previous failures.",
                hostname, retryAt);
            return null;
        }

        if (!await allowList.IsAllowedAsync(hostname, ct))
        {
            // Not recorded as a failure. An unknown hostname is the normal case on a public IP —
            // scanners, stale DNS, someone else's misconfigured CNAME — and counting it would
            // fill the table with rows for domains nobody has ever claimed.
            logger.LogDebug("Refusing to issue for {Hostname}: not a verified, linked domain.", hostname);
            return null;
        }

        // The CA's failures and ours are recorded differently, exactly as in
        // CertificateRenewalService.OrderAsync -- see the long comment there. The backoff
        // protects the ACME account's rate limit from a hostname the CA keeps refusing, so only
        // a failure the CA actually produced may extend it. Everything else is ours to fix, and
        // charging the platform hours of skipped retries for our own outage is how a fixed
        // platform stays dark all afternoon.
        IssuedCertificate issued;
        try
        {
            issued = await issuer.IssueAsync(hostname, ct);
        }
        catch (CertificateIssuanceUnavailableException ex)
        {
            logger.LogError(
                ex,
                "Cannot order a certificate for {Hostname} right now; the CA was never asked, so "
                + "this is not backed off and the next attempt will try again immediately.",
                hostname);
            metrics.EdgeCertificate("failed");
            return null;
        }
        catch (Exception ex)
        {
            // The CA said no. This is what the backoff is for.
            logger.LogError(ex, "Certificate issuance failed for {Hostname}.", hostname);
            await store.RecordFailureAsync(hostname, ex.Message, CancellationToken.None);
            metrics.EdgeCertificate("failed");
            return null;
        }

        try
        {
            await store.SaveAsync(hostname, issued.PemChain, issued.PemPrivateKey, CertificateSource.DcmsManaged, ct);
        }
        catch (Exception ex)
        {
            // Ours, not the CA's -- so no backoff, and deliberately loud: the certificate was
            // issued and then thrown away, spending the account's weekly budget for nothing.
            logger.LogError(
                ex,
                "A certificate was ISSUED for {Hostname} and could not be stored, so it is lost. "
                + "The CA counted it against the rate limit.",
                hostname);
            metrics.EdgeCertificate("failed");
            return null;
        }

        metrics.EdgeCertificate("issued");
        return await store.GetAsync(hostname, ct);
    }
}
