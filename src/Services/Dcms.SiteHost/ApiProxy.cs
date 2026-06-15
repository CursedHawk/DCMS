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
        }
    }
}
