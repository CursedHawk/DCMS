using System.Net;
using Dcms.Edge.Auth;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The edge's back-channel calls to identity: internal address, public identity.
///
/// <para>Worth its own tests because every failure here is a 500 on the public ingress rather
/// than a sign-in page, and the exception names the address that was <i>asked</i> for rather
/// than the one that came back — so the message points away from the cause.</para>
/// </summary>
public class InternalIdentityHandlerTests
{
    [Fact]
    public async Task Sends_to_the_internal_address_while_keeping_the_public_host()
    {
        var (handler, captured) = Build();
        using var client = new HttpClient(handler);

        await client.GetAsync(
            "https://auth.highgeek.eu/.well-known/openid-configuration",
            TestContext.Current.CancellationToken);

        // Out over the compose network, not out to the public name and back in through this
        // same proxy -- which works only while the host hairpins NAT on its own published port.
        captured.Single().RequestUri!.ToString()
            .Should().Be("http://identity:8080/.well-known/openid-configuration");
        captured.Single().Headers.Host.Should().Be("auth.highgeek.eu");
    }

    [Fact]
    public async Task Asserts_the_public_scheme_so_identity_advertises_https_endpoints()
    {
        // THE REGRESSION THIS EXISTS FOR. Identity builds its discovery document's endpoint URLs
        // from the incoming request's scheme and Host. Rewriting only the Host made the internal
        // call arrive as plain HTTP, so it answered with "jwks_uri": "http://auth.highgeek.eu/..."
        // -- the right host on the wrong scheme. Microsoft.IdentityModel then refuses its own
        // metadata (IDX20108), the OIDC challenge throws, and every gated route answers 500.
        var (handler, captured) = Build();
        using var client = new HttpClient(handler);

        await client.GetAsync(
            "https://auth.highgeek.eu/.well-known/openid-configuration",
            TestContext.Current.CancellationToken);

        captured.Single().Headers.GetValues("X-Forwarded-Proto").Should().ContainSingle()
            .Which.Should().Be("https");
    }

    [Fact]
    public async Task Leaves_a_call_to_anywhere_else_completely_alone()
    {
        // The rewrite is keyed on the public authority's host. Anything else is somebody else's
        // address and must not be redirected onto identity, nor told what scheme it was reached
        // over.
        var (handler, captured) = Build();
        using var client = new HttpClient(handler);

        await client.GetAsync("https://example.com/whatever", TestContext.Current.CancellationToken);

        captured.Single().RequestUri!.ToString().Should().Be("https://example.com/whatever");
        captured.Single().Headers.Contains("X-Forwarded-Proto").Should().BeFalse();
    }

    private static (InternalIdentityHandler Handler, List<HttpRequestMessage> Captured) Build()
    {
        var captured = new List<HttpRequestMessage>();
        var handler = new InternalIdentityHandler("https://auth.highgeek.eu", "http://identity:8080")
        {
            InnerHandler = new CapturingHandler(captured),
        };
        return (handler, captured);
    }

    private sealed class CapturingHandler(List<HttpRequestMessage> captured) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            captured.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
