using Dcms.Shared.Caching;

namespace Dcms.Edge.Certificates;

/// <summary>
/// Holds the HTTP-01 key authorizations the CA comes back to fetch.
///
/// <para>In Redis rather than in memory, because the CA's validation request is a fresh
/// connection from the internet and lands on whichever edge replica the load balancer picks —
/// not necessarily the one that started the order. With per-process state, issuance would
/// succeed or fail depending on that coin flip, and the failure would look like a DNS or
/// firewall problem.</para>
///
/// <para>Short TTL: a token is useful for the seconds between publishing it and the CA
/// validating it. Anything still present after that is either a failed order or litter, and a
/// stale key authorization left lying around is a small but real ACME-account risk.</para>
/// </summary>
public sealed class AcmeChallengeStore(ICacheService cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public Task PublishAsync(string token, string keyAuthorization, CancellationToken ct)
        => cache.SetAsync(Key(token), keyAuthorization, Ttl, ct);

    public Task<string?> GetAsync(string token, CancellationToken ct)
        => cache.GetAsync<string>(Key(token), ct);

    public Task RemoveAsync(string token, CancellationToken ct)
        => cache.RemoveAsync(Key(token), ct);

    private static string Key(string token) => $"acme:http01:{token}";
}
