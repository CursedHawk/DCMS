using Dcms.Edge;
using Dcms.Edge.Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// Which hostnames the edge will spend an ACME order on.
/// </summary>
public class TlsAllowListTests
{
    [Theory]
    [InlineData("admin.highgeek.eu")]
    [InlineData("platform.highgeek.eu")]
    [InlineData("auth.highgeek.eu")]
    [InlineData("grafana.highgeek.eu")]
    [InlineData("git.highgeek.eu")]
    [InlineData("ADMIN.HIGHGEEK.EU")]
    public async Task Always_allows_the_platforms_own_hostnames(string hostname)
    {
        // These are not rows in tenancy.domains and never will be, so site-host has correctly
        // never heard of them. Without this the edge would import them at cutover and then never
        // renew one -- and every operator hostname would go to a browser warning on the same
        // afternoon about sixty days later, with nothing having changed to explain it.
        var allowList = Build(siteHostAnswers: false);

        (await allowList.IsAllowedAsync(hostname, TestContext.Current.CancellationToken))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Refuses_a_stranger_when_site_host_does_not_recognise_it()
    {
        // A public IP attracts hostnames strangers have pointed at it. Issuing for them spends
        // this account's weekly budget on their domains.
        (await Build(siteHostAnswers: false)
                .IsAllowedAsync("someone-elses-domain.example", TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Refuses_rather_than_guesses_when_site_host_cannot_be_reached()
    {
        // Refused, not allowed. If we cannot tell whether a hostname is ours, issuing anyway is
        // the failure that ends in a rate-limit block; refusing costs one visitor a handshake.
        (await Build(siteHostAnswers: null)
                .IsAllowedAsync("shop.tenant.example", TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Allows_a_tenant_domain_site_host_recognises()
    {
        (await Build(siteHostAnswers: true)
                .IsAllowedAsync("shop.tenant.example", TestContext.Current.CancellationToken))
            .Should().BeTrue();
    }

    private static TlsAllowList Build(bool? siteHostAnswers)
    {
        var http = new HttpClient(new StubHandler(siteHostAnswers));
        return new TlsAllowList(
            http,
            Options.Create(new CertificateOptions()),
            Options.Create(new EdgeOptions()),
            NullLogger<TlsAllowList>.Instance);
    }

    /// <summary>null means site-host is unreachable, which is a different answer from "no".</summary>
    private sealed class StubHandler(bool? allowed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => allowed switch
            {
                null => throw new HttpRequestException("site-host is down"),
                true => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)),
                false => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)),
            };
    }
}
