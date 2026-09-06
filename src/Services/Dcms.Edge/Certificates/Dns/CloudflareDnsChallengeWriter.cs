using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates.Dns;

/// <summary>
/// Publishes DNS-01 challenge records through the Cloudflare API.
///
/// <para>Cloudflare because that is where <c>highgeek.eu</c>'s zone lives, and DNS-only — the
/// records resolve straight to the origin with no proxying — so the edge genuinely terminates
/// TLS and this is only ever asked to write a TXT record.</para>
///
/// <para><b>Records are created, never updated.</b> See <see cref="IDnsChallengeWriter"/> for
/// why: an order covering an apex and its wildcard needs two different values live at the same
/// name simultaneously, and <c>PUT</c> would leave one of them satisfying an authorization that
/// had already been abandoned.</para>
/// </summary>
public sealed class CloudflareDnsChallengeWriter(
    HttpClient http,
    IOptions<DnsOptions> options,
    ILogger<CloudflareDnsChallengeWriter> logger) : IDnsChallengeWriter
{
    /// <summary>
    /// Zone name to zone id. Cached for the life of the process: a zone's id does not change,
    /// and the alternative is two extra API calls on every authorization of every order.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> zoneIds = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DnsChallengeRecord> AddTxtAsync(string name, string value, CancellationToken ct)
    {
        var zoneId = await ResolveZoneIdAsync(name, ct)
                     ?? throw new CertificateIssuanceUnavailableException(
                         $"No Cloudflare zone in this account contains '{name}', so the DNS-01 "
                         + "challenge cannot be published. Add the zone, or remove the identifier "
                         + "from the managed certificate.");

        using var request = Authorized(HttpMethod.Post, $"zones/{zoneId}/dns_records");
        request.Content = JsonContent.Create(new
        {
            type = "TXT",
            name,
            content = value,
            ttl = options.Value.Cloudflare.RecordTtlSeconds,
            comment = "DCMS ACME DNS-01 challenge; removed automatically.",
        });

        var result = await SendAsync<CloudflareRecord>(request, $"create the TXT record {name}", ct);
        logger.LogInformation("Published DNS-01 challenge record {Name} in zone {ZoneId}.", name, zoneId);
        return new DnsChallengeRecord(zoneId, result.Id, name);
    }

    /// <summary>
    /// Removes a challenge record. Never throws: this runs in the <c>finally</c> of an issuance,
    /// and a certificate that was successfully obtained must not be lost because tidying up
    /// afterwards failed. A record left behind is inert — the token in it is already spent.
    /// </summary>
    public async Task RemoveAsync(DnsChallengeRecord record, CancellationToken ct)
    {
        try
        {
            using var request = Authorized(
                HttpMethod.Delete, $"zones/{record.ZoneId}/dns_records/{record.RecordId}");
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Could not remove the DNS-01 challenge record {Name} ({Status}). It is inert "
                    + "-- the token is spent -- but should be tidied up by hand if these accumulate.",
                    record.Name, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove the DNS-01 challenge record {Name}.", record.Name);
        }
    }

    public async Task<bool> CanPublishForAsync(string identifier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Cloudflare.ApiToken))
        {
            return false;
        }

        try
        {
            return await ResolveZoneIdAsync(DnsChallenge.BaseDomain(identifier), ct) is not null;
        }
        catch (Exception ex)
        {
            // Asked while somebody is typing into a form. Cloudflare being briefly unreachable
            // should not be reported to them as "you do not own that domain".
            logger.LogWarning(ex, "Could not check Cloudflare for a zone covering {Identifier}.", identifier);
            throw;
        }
    }

    /// <summary>
    /// Finds the zone that contains <paramref name="name"/> by walking labels upward:
    /// <c>_acme-challenge.dcms.highgeek.eu</c> → <c>dcms.highgeek.eu</c> → <c>highgeek.eu</c>,
    /// which is where it stops.
    ///
    /// <para>Walking rather than assuming the last two labels, because a zone is not always
    /// registrable-domain shaped — <c>dcms.highgeek.eu</c> could be delegated as its own zone
    /// tomorrow, and then the challenge belongs there and not in the parent. Asking is cheap and
    /// cached; guessing is wrong exactly when someone has just changed the DNS layout.</para>
    /// </summary>
    private async Task<string?> ResolveZoneIdAsync(string name, CancellationToken ct)
    {
        var labels = name.TrimEnd('.').Split('.');

        // Stop at two labels: nothing shorter is a registrable zone, and asking Cloudflare for
        // "eu" is a wasted round trip on every lookup that is going to fail anyway.
        for (var i = 0; i + 2 <= labels.Length; i++)
        {
            var candidate = string.Join('.', labels.Skip(i));
            if (zoneIds.TryGetValue(candidate, out var cached))
            {
                return cached;
            }

            using var request = Authorized(HttpMethod.Get, $"zones?name={Uri.EscapeDataString(candidate)}&status=active");
            var zones = await SendAsync<CloudflareRecord[]>(request, $"look up the zone {candidate}", ct);
            if (zones.Length > 0)
            {
                zoneIds[candidate] = zones[0].Id;
                return zones[0].Id;
            }
        }

        return null;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var config = options.Value.Cloudflare;
        if (string.IsNullOrWhiteSpace(config.ApiToken))
        {
            // Its own exception type, so the caller records a failure the CA was never asked
            // about: no backoff, and nothing spent against the rate limit.
            throw new CertificateIssuanceUnavailableException(
                "Edge:Dns:Cloudflare:ApiToken is not set, so no DNS-01 challenge can be published "
                + "and no wildcard certificate can be issued. Put a scoped Cloudflare token "
                + "(Zone:DNS:Edit + Zone:Zone:Read) in Vault at secret/dcms/edge as "
                + "Edge__Dns__Cloudflare__ApiToken.");
        }

        var request = new HttpRequestMessage(method, $"{config.ApiBase.TrimEnd('/')}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiToken);
        return request;
    }

    /// <summary>
    /// Sends and unwraps Cloudflare's envelope.
    ///
    /// <para>Cloudflare answers 200 with <c>"success": false</c> for some failures, so the status
    /// code alone is not the answer. Its <c>errors</c> array carries the sentence worth showing —
    /// "Zone Not Found", "Invalid TTL" — and that is lifted into the message, because it ends up
    /// on the certificate row and in the console, where somebody has to act on it.</para>
    /// </summary>
    private async Task<T> SendAsync<T>(HttpRequestMessage request, string what, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        var envelope = await response.Content.ReadFromJsonAsync<CloudflareEnvelope<T>>(ct);

        if (envelope is null || !envelope.Success || envelope.Result is null)
        {
            var detail = envelope?.Errors is { Length: > 0 } errors
                ? string.Join("; ", errors.Select(e => $"{e.Code} {e.Message}"))
                : $"HTTP {(int)response.StatusCode}";

            throw new CertificateIssuanceUnavailableException(
                $"Cloudflare refused to {what}: {detail}. Check Edge:Dns:Cloudflare:ApiToken and "
                + "that it is scoped to this zone.");
        }

        return envelope.Result;
    }

    private sealed record CloudflareEnvelope<T>(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("result")] T? Result,
        [property: JsonPropertyName("errors")] CloudflareError[]? Errors);

    private sealed record CloudflareError(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string Message);

    private sealed record CloudflareRecord([property: JsonPropertyName("id")] string Id);
}
