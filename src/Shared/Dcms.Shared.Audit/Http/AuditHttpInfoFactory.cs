using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Dcms.Shared.Audit.Http;

/// <summary>
/// Reads the request shape into <see cref="AuditHttpInfo"/>.
///
/// <para>Built twice per request on purpose. The endpoint filter builds it <i>before</i> the
/// handler, so an entry that commits inside the handler's transaction still carries the route,
/// the caller and the address; the middleware rebuilds it afterwards with the status code, for
/// the entries still buffered at flush. A record written atomically with its change therefore
/// has no status code — which is honest: at the moment it was written, there was not one yet.</para>
/// </summary>
public static class AuditHttpInfoFactory
{
    private const int MaxUserAgentLength = 512;

    public static AuditHttpInfo Build(HttpContext context, bool includeStatus) => new(
        Method: context.Request.Method,
        // The route pattern, not the URL: "/api/admin/sites/{id}" groups usefully and keeps
        // identifiers out of a column that would otherwise duplicate ResourceId.
        RoutePattern: (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText,
        StatusCode: includeStatus ? context.Response.StatusCode : null,
        IpAddress: context.Connection.RemoteIpAddress?.ToString(),
        // Every service that runs this middleware trusts all forwarding proxies today, so the
        // address is an assertion by the caller unless it provably passed the edge. Recorded,
        // and marked for what it is. Pinning KnownIPNetworks is a separate change.
        IpTrusted: false,
        UserAgent: Truncate(context.Request.Headers.UserAgent.ToString()));

    private static string? Truncate(string? value) =>
        string.IsNullOrEmpty(value) ? null
        : value.Length <= MaxUserAgentLength ? value
        : value[..MaxUserAgentLength];
}
