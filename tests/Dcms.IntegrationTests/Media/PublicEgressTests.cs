extern alias AdminApiApp;
using System.Net;
using AdminApiApp::Dcms.AdminApi.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Dcms.IntegrationTests.Media;

/// <summary>Plugin media imports reach the public internet only — never the compose network.</summary>
public class PublicEgressTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.5")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("224.0.0.1")]
    public void Internal_addresses_are_not_public(string address) =>
        PublicEgress.IsPublic(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700::1111")]
    public void Internet_addresses_are(string address) =>
        PublicEgress.IsPublic(IPAddress.Parse(address)).Should().BeTrue();

    [Fact]
    public async Task A_loopback_server_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapGet("/secret", () => "internal");
        await app.StartAsync(ct);
        var local = app.Urls.First();

        using var client = new HttpClient(PublicEgress.Handler(allowPrivate: false));
        var direct = () => client.GetAsync($"{local}/secret", ct);
        await direct.Should().ThrowAsync<HttpRequestException>().WithMessage("*not resolve to a public address*");

        using var allowed = new HttpClient(PublicEgress.Handler(allowPrivate: true));
        (await allowed.GetStringAsync($"{local}/secret", ct)).Should().Be("internal", "the test switch does let local targets through");
    }
}
