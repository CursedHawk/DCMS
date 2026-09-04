using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Dcms.PlatformApi.Observability;

/// <summary>
/// Truncates one container's Docker json log, through the sidecar that is allowed to.
///
/// <para><b>Why a sidecar at all.</b> The Docker API has no "clear this container's logs" call —
/// the logs are files under <c>/var/lib/docker/containers</c>, owned by root, and the only way
/// to empty one is to truncate it. platform-api must not be the process holding that, so the
/// capability is isolated in a container that does nothing else and is off by default.</para>
///
/// <para>Worth keeping in proportion: <c>x-logging</c> already caps these at 3 × 50 MB per
/// container, so this is a convenience under disk pressure rather than a leak being plugged.
/// It exists because an operator asked for it; it is switched off unless a deployment opts in.</para>
/// </summary>
public sealed class LogJanitorClient(HttpClient http, IOptions<ObservabilityOptions> options)
{
    public async Task<long> TruncateAsync(string container, CancellationToken ct)
    {
        // StringContent, not JsonContent, and the reason is not style. JsonContent cannot
        // compute its own length, so HttpClient sends it with Transfer-Encoding: chunked — and
        // the janitor is a Python BaseHTTPRequestHandler, which does not decode chunked bodies.
        // It read zero bytes and reported an empty request, which looked exactly like a
        // serialisation bug on this side. StringContent sets Content-Length.
        var payload = JsonSerializer.Serialize(new Dictionary<string, string> { ["container"] = container });
        var request = new HttpRequestMessage(HttpMethod.Post, "/truncate")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", options.Value.LogJanitorSecret);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"log-janitor refused the request ({(int)response.StatusCode}): {body}");
        }

        var doc = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return doc.TryGetProperty("freedBytes", out var freed) && freed.TryGetInt64(out var bytes)
            ? bytes
            : 0;
    }
}
