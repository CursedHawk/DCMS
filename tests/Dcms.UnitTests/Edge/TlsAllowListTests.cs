using System.Net.Http.Json;
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

    [Fact]
    public async Task Lists_the_platform_hostnames_alongside_the_tenant_domains()
    {
        // This list is what the renewal sweep backfills from: anything on it with no row in
        // edge.certificates gets an ACME order on the next pass. site-host contributes only
        // tenant domains -- the platform's own names are not rows in tenancy.domains -- so if
        // they were not added here the sweep would never issue one, and the operator plane
        // would depend on EdgeTlsPreflight's single startup pass having gone right.
        var hostnames = await Build(siteHostAnswers: true, tenantDomains: ["shop.tenant.example"])
            .AllowedHostnamesAsync(TestContext.Current.CancellationToken);

        hostnames.Should().Contain([
            "admin.highgeek.eu", "platform.highgeek.eu", "auth.highgeek.eu",
            "grafana.highgeek.eu", "git.highgeek.eu", "shop.tenant.example",
        ]);
    }

    [Fact]
    public async Task Still_lists_the_platform_hostnames_when_site_host_is_unreachable()
    {
        // The tenant half is postponed; the operator half is not. An edge that dropped every
        // hostname because site-host was restarting would stop renewing the console it is
        // diagnosed from -- and site-host being down is exactly when that matters.
        var hostnames = await Build(siteHostAnswers: null)
            .AllowedHostnamesAsync(TestContext.Current.CancellationToken);

        hostnames.Should().Contain("admin.highgeek.eu").And.Contain("grafana.highgeek.eu");
    }

    [Fact]
    public async Task Asks_site_host_for_the_hostname_list_at_its_sibling_endpoint()
    {
        // Derived from TlsAllowedEndpoint rather than configured separately. Two settings that
        // must agree is one more way for a pair to drift, and this drift is silent: the
        // backfill simply never finds anything to do.
        var handler = new StubHandler(true, ["shop.tenant.example"]);
        var allowList = new TlsAllowList(
            new HttpClient(handler),
            Options.Create(new CertificateOptions()),
            Options.Create(new EdgeOptions()),
            NullLogger<TlsAllowList>.Instance);

        await allowList.AllowedHostnamesAsync(TestContext.Current.CancellationToken);

        handler.LastUrl.Should().Be("http://site-host:8080/internal/tls-hostnames");
    }

    private static TlsAllowList Build(bool? siteHostAnswers, string[]? tenantDomains = null)
    {
        var http = new HttpClient(new StubHandler(siteHostAnswers, tenantDomains));
        return new TlsAllowList(
            http,
            Options.Create(new CertificateOptions()),
            Options.Create(new EdgeOptions()),
            NullLogger<TlsAllowList>.Instance);
    }

    /// <summary>null means site-host is unreachable, which is a different answer from "no".</summary>
    private sealed class StubHandler(bool? allowed, string[]? tenantDomains = null) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.ToString();

            if (allowed is null)
            {
                throw new HttpRequestException("site-host is down");
            }

            if (LastUrl?.Contains("/internal/tls-hostnames", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(tenantDomains ?? []),
                });
            }

            return Task.FromResult(new HttpResponseMessage(
                allowed is true ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.NotFound));
        }
    }
}
