using Dcms.Edge;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates;

/// <summary>
/// Decides whether a hostname may be issued a certificate at all, by asking site-host — the
/// same <c>/internal/tls-allowed</c> endpoint Caddy's <c>on_demand_tls ask</c> calls today. It
/// answers 200 only for a verified domain linked to a site.
///
/// <para>Asked over HTTP rather than answered from the tenancy tables directly, and that is a
/// deliberate limit on this process. The edge is the most exposed thing on the platform; giving
/// it a Postgres role that can read <c>tenancy.domains</c> would widen what a compromise there
/// reaches, for a fact one internal call already provides. Its database role is scoped to the
/// <c>edge</c> schema alone, following the site-builder precedent.</para>
///
/// <para>Without this gate the edge would mint a certificate for any hostname pointed at its IP
/// — an open relay for someone else's ACME rate limit, and eventually for ours.</para>
///
/// <para><b>The platform's own hostnames never reach that call, and must not.</b> They are not
/// rows in <c>tenancy.domains</c> — they are what the operator configured this edge to answer
/// for — so site-host has correctly never heard of them and would refuse every one. Caddy had no
/// equivalent problem because each was a named site block; here, without this, admin.,
/// platform., grafana., auth. and git. would be imported at cutover and then never renew, and
/// every operator hostname would go to a browser warning on the same afternoon about sixty days
/// later. That is precisely the failure the renewal sweep exists to make visible, arriving
/// through the one door the sweep cannot see.</para>
/// </summary>
public sealed class TlsAllowList(
    HttpClient http,
    IOptions<CertificateOptions> options,
    IOptions<EdgeOptions> edge,
    ILogger<TlsAllowList> logger) : ITlsAllowList
{
    public async Task<bool> IsAllowedAsync(string hostname, CancellationToken ct)
    {
        if (edge.Value.PlatformHostnames.Any(
                h => string.Equals(h, hostname, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var endpoint = options.Value.TlsAllowedEndpoint;
        try
        {
            var url = $"{endpoint}?domain={Uri.EscapeDataString(hostname)}";
            using var response = await http.GetAsync(url, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            // Refused, not allowed. If we cannot tell whether a hostname is ours, issuing anyway
            // is the failure that ends in a rate-limit block; refusing costs one visitor a
            // handshake until site-host is back.
            logger.LogWarning(ex, "Could not check the TLS allow-list for {Hostname}; refusing.", hostname);
            return false;
        }
    }
}
