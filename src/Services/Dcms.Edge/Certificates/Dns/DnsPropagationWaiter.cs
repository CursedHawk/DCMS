using System.Net;
using DnsClient;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates.Dns;

/// <summary>
/// Waits until a published TXT value is visible on <b>every authoritative nameserver</b> for its
/// zone, before the CA is asked to validate.
///
/// <para><b>Why authoritative and not the system resolver.</b> Let's Encrypt queries the zone's
/// authoritative servers directly, from several network vantage points. A recursive resolver
/// answers from its cache, and the cache entry that matters here is the <i>negative</i> one:
/// something looked up <c>_acme-challenge.highgeek.eu</c> a minute ago, got NXDOMAIN, and will
/// keep saying NXDOMAIN for the negative TTL. Asking it would report the opposite of what the CA
/// is about to see — either failing a wait that should have succeeded, or worse, on a resolver
/// that happens to be warm, passing one that should have waited.</para>
///
/// <para><b>Why every server and not the first to answer.</b> The CA picks its own vantage
/// points; agreeing with one of Cloudflare's two nameservers proves nothing about the other.
/// A failed authorization costs one of the five Let's Encrypt allows per identifier per hour,
/// so the asymmetry is stark: waiting is a background minute, guessing wrong is a rate limit.
/// </para>
///
/// <para>Not admin-api's <c>DnsTxtLookup</c>, which is a plain system-resolver query and right
/// for what it does — proving a tenant put a verification record somewhere, where an answer a
/// few minutes stale is harmless. This one is racing a write we made seconds ago against a
/// validator we do not control, which is the case a cache exists to get wrong.</para>
/// </summary>
public sealed class DnsPropagationWaiter(
    IOptions<DnsOptions> options,
    TimeProvider clock,
    ILogger<DnsPropagationWaiter> logger)
{
    /// <summary>
    /// Blocks until every authoritative server for <paramref name="recordName"/>'s zone returns
    /// all of <paramref name="expectedValues"/>, or the timeout expires.
    ///
    /// <para>All of them, not any: an apex-plus-wildcard order publishes two values at one name
    /// and both authorizations are validated against whatever is live at that moment. Returning
    /// once the first value appeared would validate one authorization and race the other.</para>
    ///
    /// <para>A timeout is logged and returns false rather than throwing — the caller may still
    /// choose to ask the CA, and a validation the CA performs is a better source of truth than
    /// our own resolver failing to see a record.</para>
    /// </summary>
    public async Task<bool> WaitAsync(
        string recordName, IReadOnlyCollection<string> expectedValues, CancellationToken ct)
    {
        var config = options.Value;
        var deadline = clock.GetUtcNow().AddSeconds(config.PropagationTimeoutSeconds);
        var poll = TimeSpan.FromSeconds(Math.Max(1, config.PropagationPollSeconds));

        var authoritative = await ResolveAuthoritativeServersAsync(recordName, ct);
        if (authoritative.Count == 0)
        {
            // Not fatal. The zone's NS records being unreadable from here says something about
            // this container's network, not about what the CA will see.
            logger.LogWarning(
                "Could not find the authoritative nameservers for {Record}; not waiting for "
                + "propagation. If validation fails, this is the first thing to look at.",
                recordName);
            return false;
        }

        while (true)
        {
            if (await AllServersHaveValuesAsync(authoritative, recordName, expectedValues, ct))
            {
                logger.LogInformation(
                    "{Count} value(s) at {Record} are live on all {Servers} authoritative nameserver(s).",
                    expectedValues.Count, recordName, authoritative.Count);
                return true;
            }

            if (clock.GetUtcNow() >= deadline)
            {
                logger.LogWarning(
                    "Timed out after {Seconds}s waiting for {Record} to carry {Count} value(s) on "
                    + "all authoritative nameservers. Asking the CA anyway.",
                    config.PropagationTimeoutSeconds, recordName, expectedValues.Count);
                return false;
            }

            await Task.Delay(poll, ct);
        }
    }

    private async Task<bool> AllServersHaveValuesAsync(
        IReadOnlyList<IPAddress> servers,
        string recordName,
        IReadOnlyCollection<string> expected,
        CancellationToken ct)
    {
        foreach (var server in servers)
        {
            // UseCache off, deliberately: this client is asking authoritative servers precisely
            // to avoid a cached answer, and its own cache would reintroduce the problem.
            var client = new LookupClient(new LookupClientOptions(server)
            {
                UseCache = false,
                UseTcpFallback = true,
                Timeout = TimeSpan.FromSeconds(5),
            });

            try
            {
                var response = await client.QueryAsync(recordName, QueryType.TXT, cancellationToken: ct);
                var seen = response.Answers
                    .TxtRecords()
                    .SelectMany(r => r.Text)
                    .ToHashSet(StringComparer.Ordinal);

                if (!expected.All(seen.Contains))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Authoritative server {Server} did not answer for {Record} yet.",
                    server, recordName);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the addresses of the nameservers authoritative for the record's zone, by asking the
    /// system resolver for NS records and walking up the labels until a zone answers.
    ///
    /// <para>The system resolver is fine for <i>this</i> — NS records for a zone that has existed
    /// for years are not the thing racing a write we made seconds ago.</para>
    /// </summary>
    private async Task<IReadOnlyList<IPAddress>> ResolveAuthoritativeServersAsync(
        string recordName, CancellationToken ct)
    {
        var system = new LookupClient();
        var labels = recordName.TrimEnd('.').Split('.');

        for (var i = 0; i + 2 <= labels.Length; i++)
        {
            var zone = string.Join('.', labels.Skip(i));
            try
            {
                var ns = await system.QueryAsync(zone, QueryType.NS, cancellationToken: ct);
                var names = ns.Answers.NsRecords().Select(r => r.NSDName.Value).ToList();
                if (names.Count == 0)
                {
                    continue;
                }

                var addresses = new List<IPAddress>();
                foreach (var name in names)
                {
                    var a = await system.QueryAsync(name, QueryType.A, cancellationToken: ct);
                    addresses.AddRange(a.Answers.ARecords().Select(r => r.Address));
                }

                if (addresses.Count > 0)
                {
                    return addresses;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "No NS records for {Zone}; trying the parent.", zone);
            }
        }

        return [];
    }
}
