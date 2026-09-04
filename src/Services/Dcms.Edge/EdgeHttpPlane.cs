using Dcms.Edge.Certificates;
using Dcms.Shared.Telemetry;
using Microsoft.Extensions.Options;

namespace Dcms.Edge;

/// <summary>
/// The plain-HTTP half of the edge: what happens on port 80 once the edge owns it.
/// </summary>
public static class EdgeHttpPlane
{
    /// <summary>
    /// Redirects HTTP to HTTPS, with two exemptions that both matter.
    ///
    /// <para><b>The ACME challenge is not redirected.</b> RFC 8555's HTTP-01 validation is
    /// performed over plain HTTP by definition — a certificate authority checking whether we
    /// control a name cannot be asked to trust the certificate for that name first. Redirecting
    /// it makes issuance fail for every new domain, and the CA's error says nothing about a
    /// redirect.</para>
    ///
    /// <para><b>Health probes are not redirected.</b> The compose healthcheck reaches
    /// <c>/health</c> over HTTP on the container's own port; a 308 there reads as an unhealthy
    /// container, and a deploy would roll the edge back for being healthy.</para>
    ///
    /// <para>308 rather than 301: it preserves the method and body, so a POST that arrived on
    /// HTTP is retried as a POST. 301 turns it into a GET and the request is silently lost.</para>
    /// </summary>
    public static IApplicationBuilder UseEdgeHttpsRedirection(this IApplicationBuilder app)
    {
        var certificates = app.ApplicationServices.GetRequiredService<IOptions<CertificateOptions>>().Value;
        if (!certificates.TlsEnabled)
        {
            // Nothing to redirect to. While Caddy still terminates TLS, the edge is reached over
            // plain HTTP on the internal network and every request would bounce forever.
            return app;
        }

        var httpPort = certificates.HttpPort;
        var publicHttpsPort = certificates.PublicHttpsPort;

        return app.Use(async (context, next) =>
        {
            var isPlainHttpPort = context.Connection.LocalPort == httpPort && !context.Request.IsHttps;
            var isExempt = context.Request.Path.StartsWithSegments("/.well-known/acme-challenge")
                           || context.Request.Path.StartsWithSegments("/health");

            if (!isPlainHttpPort || isExempt)
            {
                await next();
                return;
            }

            // The PUBLIC port, not the listener's. The container listens on an unprivileged
            // port and Docker publishes 443 to it, so the listener number is the one thing that
            // must not appear here — a browser sent to https://host:8443/ arrives where nothing
            // is published, and it looks like the site is down.
            var port = publicHttpsPort == 443 ? string.Empty : $":{publicHttpsPort}";
            context.Response.Redirect(
                $"https://{context.Request.Host.Host}{port}{context.Request.Path}{context.Request.QueryString}",
                permanent: true, preserveMethod: true);
        });
    }

    /// <summary>
    /// Adds HSTS to HTTPS responses when a max-age is configured.
    ///
    /// <para>Off unless asked for. HSTS is a promise a browser remembers for as long as it says,
    /// on a domain the tenant owns and we merely serve — and undoing it takes as long as the
    /// max-age, during which their site is unreachable over plain HTTP no matter what they do.
    /// Making that commitment on a tenant's behalf by default is not ours to make; Phase 5 turns
    /// it into a per-domain setting they choose.</para>
    /// </summary>
    public static IApplicationBuilder UseEdgeHsts(this IApplicationBuilder app)
    {
        var maxAge = app.ApplicationServices.GetRequiredService<IOptions<CertificateOptions>>().Value.HstsMaxAgeSeconds;
        if (maxAge <= 0)
        {
            return app;
        }

        return app.Use(async (context, next) =>
        {
            if (context.Request.IsHttps)
            {
                context.Response.Headers["Strict-Transport-Security"] = $"max-age={maxAge}";
            }
            await next();
        });
    }

    /// <summary>
    /// Counts requests by host and status class, which is the edge's own view of traffic.
    ///
    /// <para>Deliberately its own counter rather than a lift of ASP.NET's
    /// <c>http.server.request.duration</c>: that one carries no hostname, and on the public edge
    /// "which site is being hit" is the first question anyone asks. See
    /// <see cref="DcmsMetrics.EdgeRequest"/> for why the hostname label is an accepted cost.</para>
    /// </summary>
    public static IApplicationBuilder UseEdgeRequestMetrics(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            finally
            {
                var metrics = context.RequestServices.GetRequiredService<DcmsMetrics>();
                var host = CertificateStore.Normalize(context.Request.Host.Host);
                metrics.EdgeRequest(host, $"{context.Response.StatusCode / 100}xx");
            }
        });
}
