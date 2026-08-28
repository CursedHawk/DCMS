using System.Net.Sockets;
using Yarp.ReverseProxy.Forwarder;

namespace Dcms.SiteHost;

/// <summary>
/// Reverse-proxies /api and /hub from a tenant domain to content-api, injecting
/// the resolved tenant slug as X-Dcms-Tenant so content-api applies the right
/// tenant context. Uses YARP's direct forwarder for per-request header control.
/// </summary>
public static class ApiProxy
{
    public static IEndpointRouteBuilder MapApiProxy(this IEndpointRouteBuilder app, string contentApiBaseUrl)
    {
        var forwarder = app.ServiceProvider.GetRequiredService<IHttpForwarder>();
        var httpClient = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        });

        var requestConfig = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(100) };

        async Task Forward(HttpContext context)
        {
            var resolver = context.RequestServices.GetRequiredService<DomainResolver>();
            var route = await resolver.ResolveAsync(context.Request.Host.Value ?? string.Empty, context.RequestAborted);
            if (route is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var transformer = new TenantHeaderTransformer(route.TenantSlug);
            await forwarder.SendAsync(context, contentApiBaseUrl, httpClient, requestConfig, transformer);
        }

        app.Map("/api/{**catch-all}", Forward);
        app.Map("/hub/{**catch-all}", Forward);
        return app;
    }

    private sealed class TenantHeaderTransformer(string tenantSlug) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            proxyRequest.Headers.Remove("X-Dcms-Tenant");
            proxyRequest.Headers.Add("X-Dcms-Tenant", tenantSlug);

            // YARP's default transformer copies headers but adds no X-Forwarded-* of its own,
            // and UseForwardedHeaders upstream has already consumed Caddy's. Restate them from
            // what site-host resolved, so content-api's rate limiter and audit trail see the
            // visitor rather than this proxy. Set rather than appended: the value is the one
            // address site-host actually trusts, and a chain a client could prepend to is not.
            proxyRequest.Headers.Remove("X-Forwarded-For");
            if (httpContext.Connection.RemoteIpAddress is { } clientIp)
            {
                // Bracketed for IPv6 — the form ForwardedHeadersMiddleware parses without
                // having to guess whether a trailing ":1" is a port or the last hextet.
                var value = clientIp.AddressFamily == AddressFamily.InterNetworkV6
                    ? $"[{clientIp}]"
                    : clientIp.ToString();
                proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", value);
            }
            proxyRequest.Headers.Remove("X-Forwarded-Proto");
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", httpContext.Request.Scheme);
        }
    }
}
