using Dcms.Edge;
using Dcms.Edge.Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The plain-HTTP half of the edge, which is the half that can break issuance.
/// </summary>
public class EdgeHttpPlaneTests
{
    [Fact]
    public async Task Redirects_plain_HTTP_to_HTTPS_without_naming_the_listener_port()
    {
        var context = Request("/pricing", port: 8080);
        context.Request.QueryString = new QueryString("?ref=email");

        await RunRedirect(context, new CertificateOptions { TlsEnabled = true });

        // 308, not 301: it preserves the method, so a POST that arrived on HTTP is retried as a
        // POST rather than silently becoming a GET with no body.
        context.Response.StatusCode.Should().Be(StatusCodes.Status308PermanentRedirect);
        // No :8443. The container listens unprivileged and Docker publishes 443 onto it, so the
        // listener's number is the one thing that must never reach a browser — it points at a
        // port nothing is published on, and the site looks down.
        context.Response.Headers.Location.ToString()
            .Should().Be("https://shop.tenant.example/pricing?ref=email");
    }

    [Fact]
    public async Task Never_redirects_the_ACME_challenge()
    {
        var context = Request("/.well-known/acme-challenge/some-token", port: 8080);

        var reached = await RunRedirect(context, new CertificateOptions { TlsEnabled = true });

        // HTTP-01 validation happens over plain HTTP by definition: a CA checking whether we
        // control a name cannot first be asked to trust our certificate for that name. Redirect
        // it and every new domain fails to issue, with a CA error that says nothing about a
        // redirect — which is the same trap as pointing the challenge at site-host.
        reached.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Never_redirects_a_health_probe()
    {
        var context = Request("/health", port: 8080);

        var reached = await RunRedirect(context, new CertificateOptions { TlsEnabled = true });

        // The compose healthcheck speaks plain HTTP to the container's own port. A 308 there
        // reads as an unhealthy container, and the deploy rolls back an edge that is working.
        reached.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Does_nothing_at_all_while_TLS_is_off()
    {
        var context = Request("/pricing", port: 8080);

        var reached = await RunRedirect(context, new CertificateOptions { TlsEnabled = false });

        // Before the cutover the edge is reached over plain HTTP on the internal network and
        // there is nothing to redirect to. Redirecting would bounce every request forever, and
        // it would do so on the deploy BEFORE the one anybody was watching.
        reached.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Names_a_non_standard_public_port_when_there_is_one()
    {
        var context = Request("/", port: 8080);

        await RunRedirect(context, new CertificateOptions { TlsEnabled = true, PublicHttpsPort = 8443 });

        context.Response.Headers.Location.ToString().Should().Be("https://shop.tenant.example:8443/");
    }

    [Fact]
    public async Task Sends_HSTS_only_over_HTTPS_and_only_when_asked_to()
    {
        var withPolicy = Request("/", port: 8443);
        withPolicy.Request.Scheme = "https";
        await RunHsts(withPolicy, new CertificateOptions { HstsMaxAgeSeconds = 31536000 });
        withPolicy.Response.Headers["Strict-Transport-Security"].ToString()
            .Should().Be("max-age=31536000");

        // Off by default, and that is a decision rather than an oversight: HSTS is a promise the
        // browser keeps for the full max-age on a domain the TENANT owns, and rolling back to
        // turning it off would not undo it. It is not ours to make on their behalf.
        var byDefault = Request("/", port: 8443);
        byDefault.Request.Scheme = "https";
        await RunHsts(byDefault, new CertificateOptions());
        byDefault.Response.Headers.ContainsKey("Strict-Transport-Security").Should().BeFalse();

        // And never on the plain-HTTP listener, where a man in the middle could have added it.
        var overHttp = Request("/", port: 8080);
        await RunHsts(overHttp, new CertificateOptions { HstsMaxAgeSeconds = 31536000 });
        overHttp.Response.Headers.ContainsKey("Strict-Transport-Security").Should().BeFalse();
    }

    private static DefaultHttpContext Request(string path, int port)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("shop.tenant.example");
        context.Request.Path = path;
        context.Connection.LocalPort = port;
        return context;
    }

    /// <returns>Whether the request reached the end of the pipeline instead of being redirected.</returns>
    private static async Task<bool> RunRedirect(HttpContext context, CertificateOptions options)
        => await Run(context, options, app => app.UseEdgeHttpsRedirection());

    private static async Task<bool> RunHsts(HttpContext context, CertificateOptions options)
        => await Run(context, options, app => app.UseEdgeHsts());

    private static async Task<bool> Run(
        HttpContext context, CertificateOptions options, Action<IApplicationBuilder> configure)
    {
        var collection = new ServiceCollection();
        collection.Configure<CertificateOptions>(configured =>
        {
            configured.TlsEnabled = options.TlsEnabled;
            configured.HttpPort = options.HttpPort;
            configured.PublicHttpsPort = options.PublicHttpsPort;
            configured.HstsMaxAgeSeconds = options.HstsMaxAgeSeconds;
        });

        var reached = false;
        var app = new ApplicationBuilder(collection.BuildServiceProvider());
        configure(app);
        app.Run(_ =>
        {
            reached = true;
            return Task.CompletedTask;
        });

        await app.Build()(context);
        return reached;
    }
}
