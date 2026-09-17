using System.Net;
using System.Net.Sockets;

namespace Dcms.Shared.Data.Ai;

/// <summary>
/// Validates a caller-supplied provider base URL before it is stored.
///
/// <para>A base URL is an outbound destination chosen by a tenant user, and ai-gateway
/// attaches an API key to every request it sends there. Two separate controls keep that
/// from becoming a way to harvest credentials or to reach the compose network:</para>
///
/// <list type="number">
/// <item>this check, which constrains the <i>shape</i> of the destination on write;</item>
/// <item>the layer check in <c>AiProviderResolver</c>, which refuses to pair a base URL
/// with a key that belongs to a broader scope than the URL does. That one is the real
/// defense for the key — this one is defense in depth for the request.</item>
/// </list>
///
/// <para><b>What this cannot do.</b> A hostname that resolves to an internal address is
/// still a hostname, and re-resolving at request time would not settle it either (the
/// answer can change between the check and the connection). Requiring TLS for the remote
/// providers is what actually keeps the compose network out of reach: nothing on it —
/// postgres, vault, minio, forgejo, the sibling APIs — speaks HTTPS internally.</para>
/// </summary>
public static class AiBaseUrl
{
    /// <summary>
    /// Providers that run on the operator's own network and are never sent a real
    /// credential. They are the reason this is not simply "https, public hosts only":
    /// an Ollama or LM Studio endpoint is a private, plaintext address by definition.
    /// </summary>
    private static bool IsLocalProvider(AiProvider provider) =>
        provider is AiProvider.Ollama or AiProvider.LmStudio;

    /// <summary>
    /// Returns null when <paramref name="baseUrl"/> may be stored, or a message naming
    /// the problem. An absent base URL is valid — it means "use the default".
    /// </summary>
    /// <param name="allowedLocalHosts">
    /// Operator-configured hosts (from <c>Ai:AllowedLocalHosts</c>) that a local provider
    /// (Ollama / LM Studio) may point at even though they are private/plaintext. Empty by
    /// default: without an explicit opt-in, a caller-supplied "local" base URL is validated
    /// exactly like a hosted one, so a tenant editor cannot aim the platform's egress at an
    /// internal service. (SEC-01)
    /// </param>
    public static string? Validate(
        string? baseUrl, AiProvider provider, IReadOnlySet<string>? allowedLocalHosts = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return "The base URL must be an absolute URL.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return "The base URL must use http or https.";
        }

        // Credentials in the URL would be sent to the host on every call and would sit
        // in the settings row in plaintext, outside the Transit-encrypted key field.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "The base URL must not embed credentials.";
        }

        // A local provider is only allowed its private/plaintext address when the operator has
        // explicitly allow-listed the host. This is the SSRF fix: the old code returned null
        // here for ANY local-provider URL, which let a tenant/user point base-url at
        // http://prometheus:9090, the Docker socket proxy, etc. and have ai-gateway call it.
        if (IsLocalProvider(provider)
            && allowedLocalHosts is { Count: > 0 }
            && allowedLocalHosts.Contains(uri.Host))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return "The base URL must use https (a local provider host must be added to "
                 + "Ai:AllowedLocalHosts by an operator before it can be used).";
        }

        if (IsInternalHost(uri))
        {
            return "The base URL must not point at a private or loopback address.";
        }

        return null;
    }

    /// <summary>
    /// Whether the host is one nothing outside this deployment could legitimately be.
    /// Only literal addresses and the reserved loopback names are decided here — see the
    /// class remarks for why a name that resolves inward is left to the https rule.
    /// </summary>
    private static bool IsInternalHost(Uri uri)
    {
        var host = uri.Host;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // UriHostNameType.IPv6 strips the brackets for us; IPv4 parses directly.
        if (!IPAddress.TryParse(host, out var ip))
        {
            return false;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                10 => true,                                   // 10.0.0.0/8
                127 => true,                                  // 127.0.0.0/8
                169 when b[1] == 254 => true,                 // 169.254.0.0/16 link-local (metadata)
                172 when b[1] >= 16 && b[1] <= 31 => true,     // 172.16.0.0/12
                192 when b[1] == 168 => true,                 // 192.168.0.0/16
                0 => true,                                    // 0.0.0.0/8
                _ => false,
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal)
            {
                return true;
            }
            // An IPv4-mapped address (::ffff:10.0.0.1) is the same reachability question.
            if (ip.IsIPv4MappedToIPv6)
            {
                return IsInternalHost(new UriBuilder(uri) { Host = ip.MapToIPv4().ToString() }.Uri);
            }
        }

        return false;
    }
}
