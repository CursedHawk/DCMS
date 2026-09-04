using Dcms.Edge.Transforms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The edge is the trust boundary. Every service behind it calls
/// <c>KnownProxies.Clear()</c>/<c>KnownIPNetworks.Clear()</c> — it trusts its caller completely
/// — which is safe only while the edge is the one setting the headers that carry trust. These
/// assert that a client cannot set them itself.
/// </summary>
public class HeaderScrubbingTests
{
    [Theory]
    // Forwarding metadata: the edge is the first hop, so any inbound value is a forgery. Note
    // X-Forwarded-For in particular — YARP appends to it, so a surviving client value would sit
    // at the front of the chain, and that is the address content-api's rate limiter partitions
    // on and the audit log records.
    [InlineData("X-Forwarded-For", "1.2.3.4")]
    [InlineData("X-Forwarded-Proto", "https")]
    [InlineData("X-Forwarded-Host", "admin.highgeek.eu")]
    [InlineData("X-Forwarded-Prefix", "/evil")]
    [InlineData("X-Real-IP", "1.2.3.4")]
    [InlineData("Forwarded", "for=1.2.3.4")]
    // Identity headers. Grafana and Forgejo are configured (Phase 4) to accept these as proof of
    // who the caller is, so a client-settable X-WEBAUTH-USER is a full authentication bypass.
    [InlineData("X-WEBAUTH-USER", "admin@highgeek.eu")]
    [InlineData("X-WEBAUTH-EMAIL", "admin@highgeek.eu")]
    [InlineData("X-WEBAUTH-ROLE", "Admin")]
    public async Task Strips_untrusted_headers_from_every_request(string header, string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[header] = value;

        await RunScrubber(context);

        context.Request.Headers.ContainsKey(header).Should().BeFalse(
            $"{header} carries trust the edge must be the only one to grant");
    }

    /// <summary>
    /// The operator plane's own headers are untouched: the admin SPA sends X-Dcms-Tenant on
    /// every call and TenantMembershipMiddleware authorises the caller's membership of whatever
    /// it names. Scrubbing it here would break the console outright, which is why it is handled
    /// per-route inside the proxy pipeline instead.
    /// </summary>
    [Theory]
    [InlineData("X-Dcms-Tenant", "acme")]
    [InlineData("Authorization", "Bearer token")]
    [InlineData("X-Dcms-Request-Id", "abc-123")]
    public async Task Leaves_legitimate_client_headers_alone(string header, string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[header] = value;

        await RunScrubber(context);

        context.Request.Headers[header].ToString().Should().Be(value);
    }

    private static async Task RunScrubber(HttpContext context)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseUntrustedHeaderScrubbing();
        app.Run(_ => Task.CompletedTask);
        await app.Build()(context);
    }
}
