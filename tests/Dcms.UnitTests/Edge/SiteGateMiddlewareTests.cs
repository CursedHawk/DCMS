using Dcms.Edge.Auth;
using Dcms.Edge.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Dcms.UnitTests.Edge;

/// <summary>
/// The gate as the edge runs it (ADR 0022): on what the tenant-sites route would proxy and
/// nothing else, closed until the rules are known, and refusing paths it could misread. With
/// site sign-in not configured, a gated path is a 503 — never the page.
/// </summary>
public class SiteGateMiddlewareTests
{
    private static readonly SiteGateEntry Corp = new(Guid.NewGuid(), "corp",
    [
        new SiteRule("/portal/news", SiteAccess.Public),
        new SiteRule("/portal", SiteAccess.SignedIn),
    ]);

    [Theory]
    [InlineData("corp.example", "/", 200)]
    [InlineData("corp.example", "/about", 200)]
    [InlineData("corp.example", "/portal/news/today", 200)]
    [InlineData("corp.example", "/portal", 503)]
    [InlineData("corp.example", "/portal_reports.html", 503)]
    [InlineData("CORP.example.", "/Portal/x", 503)]
    [InlineData("corp.example", "/portal%2Fx", 400)]
    [InlineData("corp.example", "/a\\portal", 400)]
    [InlineData("other.example", "/portal", 200)]
    public async Task A_tenant_site_request_meets_its_hosts_rules(string host, string path, int expected) =>
        (await SendAsync(Loaded(), host, path, tenantSite: true)).Should().Be(expected);

    [Fact]
    public async Task The_edges_own_endpoints_are_never_gated() =>
        // /.edge/site/signin is the edge's, not the tenant's: under a "/" rule it must still answer.
        (await SendAsync(Loaded(new SiteGateEntry(Corp.TenantId, "corp", [new SiteRule("/", SiteAccess.SignedIn)])),
            "corp.example", "/.edge/site/signin", tenantSite: false)).Should().Be(200);

    [Fact]
    public async Task A_path_that_only_looks_like_the_edges_is_judged_like_any_other() =>
        (await SendAsync(Loaded(new SiteGateEntry(Corp.TenantId, "corp", [new SiteRule("/", SiteAccess.SignedIn)])),
            "corp.example", "/.edge/anything", tenantSite: true)).Should().Be(503);

    [Fact]
    public async Task Before_the_rules_are_read_no_tenant_site_is_served() =>
        (await SendAsync(new SiteGates(), "anything.example", "/", tenantSite: true)).Should().Be(503);

    [Fact]
    public async Task Before_the_rules_are_read_the_platform_is_unaffected() =>
        (await SendAsync(new SiteGates(), "admin.example", "/", tenantSite: false)).Should().Be(200);

    private static SiteGates Loaded(SiteGateEntry? entry = null)
    {
        var gates = new SiteGates();
        gates.Replace(new Dictionary<string, SiteGateEntry>(StringComparer.OrdinalIgnoreCase) { ["corp.example"] = entry ?? Corp });
        return gates;
    }

    private static async Task<int> SendAsync(SiteGates gates, string host, string path, bool tenantSite)
    {
        var services = new ServiceCollection()
            .AddSingleton(gates)
            // Site sign-in not configured: the gate must refuse rather than serve.
            .AddSingleton(Options.Create(new EdgeAuthOptions()))
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseSiteGates();
        app.Run(context =>
        {
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.SetEndpoint(tenantSite
            ? new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(new RouteModel(
                new RouteConfig { RouteId = "tenant-sites", Metadata = new Dictionary<string, string> { [PlatformRoutes.PublicPlaneMetadataKey] = "true" } },
                cluster: null, HttpTransformer.Default)), "tenant-sites")
            : new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "edge"));
        await app.Build()(context);
        return context.Response.StatusCode;
    }
}
