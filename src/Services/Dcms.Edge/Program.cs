using Dcms.Edge;
using Dcms.Edge.Auth;
using Dcms.Edge.Certificates;
using Dcms.Edge.Certificates.Dns;
using Dcms.Edge.Protection;
using Dcms.Edge.Routing;
using Dcms.Edge.Transforms;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Hosting;
using Dcms.Shared.Messaging;
using Dcms.Shared.Telemetry;
using Dcms.Shared.Vault;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("edge");
builder.Services.AddDcmsMessaging(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.Configure<EdgeOptions>(builder.Configuration.GetSection(EdgeOptions.SectionName));
builder.Services.Configure<CertificateOptions>(builder.Configuration.GetSection(CertificateOptions.SectionName));
builder.Services.Configure<EdgeAuthOptions>(builder.Configuration.GetSection(EdgeAuthOptions.SectionName));

// ---- Routing ----
builder.Services.AddSingleton<DatabaseRouteSource>();
builder.Services.AddSingleton<EdgeConfigProvider>();
builder.Services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<EdgeConfigProvider>());
builder.Services.AddHostedService<EdgeConfigInvalidator>();

// ---- Certificates ----
//
// The edge's database role reaches the `edge` schema and nothing else — see
// infra/postgres/init/05-edge-role.sh. Whether a hostname may be issued a certificate is asked
// of site-host over HTTP rather than read from tenancy here, deliberately: this is the most
// exposed process on the platform and it should hold the narrowest access on it.
builder.Services.AddDcmsEdgeData(builder.Configuration);
builder.Services.AddDcmsCaching(builder.Configuration);
builder.Services.AddDcmsVaultTransit();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<CertificateStore>();
builder.Services.AddSingleton<ICertificateStore>(sp => sp.GetRequiredService<CertificateStore>());
builder.Services.AddSingleton<AcmeChallengeStore>();
builder.Services.AddSingleton<IAcmeIssuer, CertesAcmeIssuer>();
builder.Services.AddSingleton<CertificateProvisioner>();

// DNS-01, for the platform's own wildcard certificates. Let's Encrypt refuses every other
// challenge type for a wildcard identifier, so this is not an alternative to the HTTP-01 path
// above — it is the only way to hold one certificate for *.dcms.highgeek.eu instead of one per
// tenant. The Cloudflare token comes from Vault (secret/dcms/edge); absent, wildcard issuance
// refuses cleanly and everything else carries on. See ADR 0011.
builder.Services.Configure<DnsOptions>(builder.Configuration.GetSection(DnsOptions.SectionName));
builder.Services.AddHttpClient<CloudflareDnsChallengeWriter>(
    client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient<ChallTestSrvDnsChallengeWriter>(
    client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<IDnsChallengeWriter>(sp =>
{
    var dns = sp.GetRequiredService<IOptions<DnsOptions>>().Value;
    if (string.IsNullOrWhiteSpace(dns.ChallTestSrvUrl))
    {
        return sp.GetRequiredService<CloudflareDnsChallengeWriter>();
    }

    // Two switches, not one. ChallTestSrvUrl redirects where the platform proves it controls a
    // domain, so a deployment that had it set by accident would obtain certificates whose
    // validation nobody actually performed. Requiring the insecure-directory switch as well --
    // which CertesAcmeIssuer already refuses for a Let's Encrypt directory -- makes that
    // combination impossible to reach against a real CA.
    if (!sp.GetRequiredService<IOptions<CertificateOptions>>().Value.AcceptInsecureAcmeDirectory)
    {
        throw new InvalidOperationException(
            "Edge:Dns:ChallTestSrvUrl is set, which publishes ACME challenges to a mock DNS "
            + "server instead of the real zone. It is for the local acmetest profile only and "
            + "requires Edge:Certificates:AcceptInsecureAcmeDirectory as well. Unset it.");
    }

    return sp.GetRequiredService<ChallTestSrvDnsChallengeWriter>();
});
builder.Services.AddSingleton<DnsPropagationWaiter>();
builder.Services.AddSingleton<ManagedCertificateProvisioner>();
builder.Services.AddHttpClient<TlsAllowList>(client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<ITlsAllowList>(sp => sp.GetRequiredService<TlsAllowList>());
builder.Services.AddHostedService<CertificateRenewalService>();
builder.Services.AddHostedService<DomainCertificateProvisioner>();
// Acts on an operator's "renew now" for a managed certificate straight away, instead of leaving
// it to the hourly sweep. The durable flag is still the backstop.
builder.Services.AddHostedService<ManagedCertificateInvalidator>();
// Issues the platform's OWN hostnames up front, and reports the one failure that otherwise has
// no symptom other than every host refusing TLS at once. See EdgeTlsPreflight.
builder.Services.AddHostedService<EdgeTlsPreflight>();

// Opens the TLS listener when Edge:Certificates:TlsEnabled is set. Registered as an options
// configurator rather than configured inline, because the SNI callback needs the container and
// the container does not exist yet when ConfigureKestrel runs.
builder.Services.AddSingleton<IConfigureOptions<KestrelServerOptions>, EdgeTlsConfigurator>();

// A proxy that starts with an empty route table is a broken deploy that answers every request
// with a 404, and nothing about the process itself says so. Liveness is not the question here;
// "did the route table load" is.
builder.Services.AddHealthChecks()
    .AddCheck<RouteTableHealthCheck>("edge-routes", tags: ["ready"]);

// Cookie + OIDC against identity, and the two policies routes name. A no-op without a client
// secret, in which case no route carries a policy either -- see EdgeAuthOptions.ClientSecret for
// why a missing secret opens the loop rather than closing the door.
builder.AddEdgeAuthentication();

// Shedding load costs nothing downstream, and this is the only hop where the client address is
// the actual TCP peer rather than a header the edge itself assembled.
builder.AddEdgeRateLimiting();

// Off unless Edge:Cache:Enabled. See EdgeOutputCache for why a cache in front of every tenant's
// site is measured before it is trusted -- and for the one setting whose absence turns it into
// a cross-tenant content leak.
builder.AddEdgeOutputCache();

builder.Services.AddReverseProxy().AddTransforms(context =>
{
    // YARP replaces the client's Host with the destination's host unless told otherwise, and
    // that default is not survivable here:
    // site-host resolves a tenant from Request.Host, so with YARP's default every custom domain
    // would resolve to nothing and serve a 404 — a total outage of the delivery plane that no
    // health check would notice. Preserved globally, which is also what identity, admin-api,
    // Grafana and Forgejo are already configured for.
    context.AddOriginalHost(true);

    // X-Forwarded-For/Proto/Host are YARP defaults and stay on: every service behind the edge
    // reads them through UseForwardedHeaders with all proxies trusted, which is safe only
    // because UseUntrustedHeaderScrubbing below drops whatever the client sent.

    // Per-route, and only where the route asked for it: tells Grafana and Forgejo who signed in
    // here, so neither has to run its own login. See IdentityHeaders for why that is only safe
    // alongside the inbound scrubber.
    context.AddIdentityHeaders();

    // Grafana's own session cookies, deleted on the route where the edge IS the session. See
    // GrafanaSessionCookies: a stale one puts the dashboard in a permanent reload loop that no
    // amount of signing in can clear.
    context.AddGrafanaSessionCookieCleanup();
});

var app = builder.Build();

// First in the pipeline, so an exception anywhere below it becomes a ProblemDetails carrying
// the trace id instead of a bare Kestrel 500 with no body and nothing to quote.
app.UseDcmsProblemDetails();

// Before routing, and before anything can read a request header: the edge is the trust
// boundary, and everything behind it trusts its caller completely.
app.UseUntrustedHeaderScrubbing();

// After the scrubber (so the Host it counts is one a client cannot forge into a header) and
// before the redirect (so a redirected request is still counted as traffic the edge handled).
app.UseEdgeRequestMetrics();

// Exempts the ACME challenge and the health probes; see UseEdgeHttpsRedirection for why both
// of those exemptions are load-bearing rather than tidy.
app.UseEdgeHttpsRedirection();
app.UseEdgeHsts();

app.MapDcmsDefaultEndpoints();

// Before UseAuthorization, so a route carrying a policy has a principal to evaluate. YARP
// attaches RouteConfig.AuthorizationPolicy as endpoint metadata; without these two lines the
// metadata is present and nothing enforces it -- which fails open, silently, on the routes
// that matter most.
app.UseAuthentication();
app.UseAuthorization();

// After authorization, so a refused request is refused before it is counted, and a signed-in
// operator is not throttled out of the console by an anonymous flood from the same NAT.
app.UseRateLimiter();

// Before the proxy, so a cache hit is served without a downstream request at all -- which is
// the entire point. A no-op when the cache is not registered.
if (EdgeOutputCache.IsEnabled(app.Configuration))
{
    app.UseOutputCache();
}

// The edge's own handful of endpoints, namespaced under /.edge/ because every host it serves
// belongs to somebody else.
app.MapEdgeAuthEndpoints();

// Before the proxy, and matched ahead of every host route. The certificate authority fetches
// this on the domain it is validating, which is by definition a hostname the proxy would
// otherwise hand to site-host — and site-host, correctly, has never heard of it.
app.MapAcmeChallenge();

app.MapReverseProxy(proxyPipeline =>
{
    // Inside the proxy pipeline because it needs the matched route: which headers a caller may
    // set depends on whether this request came from an operator on one of our hosts or an
    // anonymous visitor on a tenant's domain.
    proxyPipeline.UsePublicPlaneHeaderScrubbing();
});

app.Run();

public partial class Program;
