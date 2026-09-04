using Dcms.Edge.Routing;
using Yarp.ReverseProxy.Model;

namespace Dcms.Edge.Transforms;

/// <summary>
/// Removes request headers a client is not allowed to supply, before anything downstream can
/// read them.
///
/// <para><b>Why this ships in the first phase, before anything trusts a header.</b> YARP's
/// default request transform copies every inbound header to the destination. Several headers on
/// this platform are trusted <i>because</i> only a proxy is supposed to set them, and every
/// service behind the edge calls <c>KnownProxies.Clear()</c> / <c>KnownIPNetworks.Clear()</c> —
/// i.e. it trusts its caller completely, which is correct only if the edge is the one setting
/// them. Copying a client's version through would turn each of those into a header a stranger
/// can choose.</para>
/// </summary>
public static class HeaderScrubbing
{
    /// <summary>
    /// Forwarding metadata. The edge is the first hop, so there is never a legitimate inbound
    /// value: whatever a client sends here is either noise or a forgery. Removed rather than
    /// overwritten because YARP's X-Forwarded-For transform <i>appends</i> by default, which
    /// would leave a client-chosen address at the front of the chain — the address content-api's
    /// rate limiter partitions on and the audit log records.
    /// </summary>
    private static readonly string[] ForwardingHeaders =
    [
        "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Prefix",
        "X-Forwarded-Port", "X-Real-IP", "Forwarded",
    ];

    /// <summary>
    /// The identity headers the edge itself will inject once it authenticates users (Phase 4:
    /// Grafana and Forgejo both accept a header as proof of identity). Scrubbed from the first
    /// deploy rather than the one that starts using them, so there is no window in which the
    /// downstream trusts a header the edge does not yet control.
    /// </summary>
    private static readonly string[] IdentityHeaders =
    [
        "X-WEBAUTH-USER", "X-WEBAUTH-EMAIL", "X-WEBAUTH-NAME", "X-WEBAUTH-ROLE", "X-WEBAUTH-GROUPS",
    ];

    /// <summary>
    /// Headers that select a tenant or a data space. These are legitimate <b>client</b> headers
    /// on the operator plane — the admin SPA sends <c>X-Dcms-Tenant</c> and
    /// <c>TenantMembershipMiddleware</c> authorises the caller's membership of whatever it names
    /// — so they are scrubbed only on public-plane routes, where the caller is an anonymous
    /// visitor and the tenant is determined by the Host header instead.
    /// </summary>
    private static readonly string[] TenantSelectionHeaders = ["X-Dcms-Tenant", "X-Dcms-Sandbox"];

    /// <summary>
    /// Strips the always-untrusted headers from every request. Register first, before routing.
    /// </summary>
    public static IApplicationBuilder UseUntrustedHeaderScrubbing(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            foreach (var header in ForwardingHeaders)
            {
                context.Request.Headers.Remove(header);
            }
            foreach (var header in IdentityHeaders)
            {
                context.Request.Headers.Remove(header);
            }
            await next();
        });

    /// <summary>
    /// Strips tenant-selection headers on routes marked as serving the public plane. Registered
    /// inside the reverse-proxy pipeline, which is the first point at which the matched route —
    /// and therefore whether this request belongs to an operator or a visitor — is known.
    ///
    /// <para>Closes a hole that predates the edge: on a tenant's public domain today, a visitor
    /// can send <c>X-Dcms-Sandbox: 1</c> and content-api routes their writes to the tenant's
    /// sandbox space. Their form submission, chat message or visitor account then never reaches
    /// the tenant's live records, and nothing about the response says so.</para>
    /// </summary>
    public static void UsePublicPlaneHeaderScrubbing(this IReverseProxyApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            var metadata = context.GetReverseProxyFeature().Route.Config.Metadata;
            if (metadata is not null
                && metadata.TryGetValue(PlatformRoutes.PublicPlaneMetadataKey, out var isPublic)
                && string.Equals(isPublic, "true", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var header in TenantSelectionHeaders)
                {
                    context.Request.Headers.Remove(header);
                }
            }
            await next();
        });
}
