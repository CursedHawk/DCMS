using System.Net.Http.Json;
using Dcms.Edge;
using Microsoft.Extensions.Options;

namespace Dcms.Edge.Certificates;

/// <summary>
/// Decides whether a hostname may be issued a certificate at all, by asking site-host's
/// <c>/internal/tls-allowed</c>. It answers 200 only for a verified domain linked to a site.
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
/// for — so site-host has correctly never heard of them and would refuse every one. Without
/// this, admin., platform., grafana., auth. and git. would never be issued a certificate at
/// all, and no amount of sweeping would fix it: the sweep asks this same list what it is
/// allowed to hold.</para>
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

    public async Task<IReadOnlyList<string>> AllowedHostnamesAsync(CancellationToken ct)
    {
        // The platform's own names FIRST, and unconditionally -- the same exemption
        // IsAllowedAsync makes, for the same reason and with the same consequence if it is
        // missed. They are not rows in tenancy.domains, so site-host contributes none of them;
        // without this line the sweep would backfill every tenant domain and no operator
        // hostname, and admin./platform./auth./grafana./git. would depend entirely on
        // EdgeTlsPreflight having succeeded on the one pass it makes at startup. Losing that
        // one pass -- Vault not yet reachable, DNS not yet pointed -- would leave the whole
        // operator plane refusing TLS with nothing that ever retries.
        var hostnames = new List<string>(edge.Value.PlatformHostnames);

        // Derived from the single-hostname endpoint's address rather than configured separately:
        // two settings that must agree is one more way for the pair to drift, and the failure
        // would be silent -- the backfill simply never finding anything to do.
        var endpoint = options.Value.TlsAllowedEndpoint.Replace(
            "/internal/tls-allowed", "/internal/tls-hostnames", StringComparison.Ordinal);
        try
        {
            hostnames.AddRange(await http.GetFromJsonAsync<string[]>(endpoint, ct) ?? []);
        }
        catch (Exception ex)
        {
            // The tenant half is skipped, the platform half is not. site-host being briefly
            // unreachable should postpone a tenant domain's certificate, never the operator
            // plane's -- and the operator plane is what the incident is diagnosed from.
            logger.LogWarning(ex, "Could not list the tenant TLS allow-list; platform hostnames only this pass.");
        }

        return hostnames;
    }
}
