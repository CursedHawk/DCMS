using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates.Dns;

/// <summary>
/// Publishes DNS-01 challenge records into <c>pebble-challtestsrv</c>, the mock DNS server the
/// Let's Encrypt team ships alongside Pebble.
///
/// <para><b>For the local ACME test harness only</b>, and refused outright against a real CA —
/// see <see cref="DnsOptions.ChallTestSrvUrl"/>. It exists because the one bug this whole DNS-01
/// path is most likely to have cannot be reproduced any other way: an order covering an apex and
/// its wildcard needs two different TXT values live at one name simultaneously, and the only way
/// to know we publish both is to watch a real ACME server validate both. challtestsrv appends
/// values per host rather than replacing them, which is exactly the behaviour a real zone has and
/// a naive implementation does not.</para>
///
/// <para>Its management API is two endpoints: <c>/set-txt</c> and <c>/clear-txt</c>.
/// <b><c>/set-txt</c> appends</b>, despite the name — it pushes onto the list of values held at
/// that host, which is the behaviour this harness is here for. Verified rather than assumed:
/// two calls for one host answer with two TXT records.</para>
/// </summary>
public sealed class ChallTestSrvDnsChallengeWriter(
    HttpClient http,
    IOptions<DnsOptions> options,
    ILogger<ChallTestSrvDnsChallengeWriter> logger) : IDnsChallengeWriter
{
    public async Task<DnsChallengeRecord> AddTxtAsync(string name, string value, CancellationToken ct)
    {
        // Fully qualified: challtestsrv keys its map on the wire-format name, so a record added
        // as "_acme-challenge.example.test" is never found for a query for the same name with
        // the root label.
        var fqdn = Fqdn(name);
        // "set" appends here; see the class remarks.
        using var response = await http.PostAsJsonAsync(
            $"{Base}/set-txt", new { host = fqdn, value }, ct);
        response.EnsureSuccessStatusCode();

        logger.LogInformation("Published DNS-01 challenge record {Name} to the test DNS server.", fqdn);
        return new DnsChallengeRecord("challtestsrv", fqdn, name);
    }

    /// <summary>
    /// Clears <b>every</b> value at the name, which is all the management API offers. Acceptable
    /// here and nowhere else: this server exists for one order at a time.
    /// </summary>
    public async Task RemoveAsync(DnsChallengeRecord record, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"{Base}/clear-txt", new { host = record.RecordId }, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Could not clear the test DNS record {Name} ({Status}).",
                    record.Name, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear the test DNS record {Name}.", record.Name);
        }
    }

    /// <summary>Anything, since the test server is authoritative for whatever it is asked about.</summary>
    public Task<bool> CanPublishForAsync(string identifier, CancellationToken ct) => Task.FromResult(true);

    private string Base => options.Value.ChallTestSrvUrl.TrimEnd('/');

    private static string Fqdn(string name) => name.EndsWith('.') ? name : name + ".";
}
