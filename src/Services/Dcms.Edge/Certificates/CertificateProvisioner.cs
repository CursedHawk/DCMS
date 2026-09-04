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
        if (await store.GetAsync(hostname, ct) is { } existing)
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

        try
        {
            var issued = await issuer.IssueAsync(hostname, ct);
            await store.SaveAsync(hostname, issued.PemChain, issued.PemPrivateKey, CertificateSource.DcmsManaged, ct);
            metrics.EdgeCertificate("issued");
            return await store.GetAsync(hostname, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Certificate issuance failed for {Hostname}.", hostname);
            await store.RecordFailureAsync(hostname, ex.Message, CancellationToken.None);
            metrics.EdgeCertificate("failed");
            return null;
        }
    }
}
