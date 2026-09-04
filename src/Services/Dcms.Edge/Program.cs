using Dcms.Edge;
using Dcms.Edge.Auth;
using Dcms.Edge.Certificates;
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
builder.Services.AddHttpClient<TlsAllowList>(client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<ITlsAllowList>(sp => sp.GetRequiredService<TlsAllowList>());
builder.Services.AddSingleton<CaddyCertificateImporter>();
builder.Services.AddHostedService<CertificateRenewalService>();
builder.Services.AddHostedService<DomainCertificateProvisioner>();

// Opens the TLS listener when Edge:Certificates:TlsEnabled is set. Registered as an options
// configurator rather than configured inline, because the SNI callback needs the container and
// the container does not exist yet when ConfigureKestrel runs.
builder.Services.AddSingleton<IConfigureOptions<KestrelServerOptions>, EdgeTlsConfigurator>();

// The check the Caddy container's probe was reaching for and could only approximate: a proxy
// that starts with an empty route table is a broken deploy that answers every request with a
// 404, and nothing about the process says so.
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
    // Caddy's reverse_proxy passes the client's Host through untouched; YARP replaces it with
    // the destination's host unless told otherwise. That difference is not cosmetic here:
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
});

var app = builder.Build();

// Before the server starts listening, not from a hosted service: a hosted service's start order
// relative to Kestrel is a detail of how the host was built, and importing certificates AFTER
// the TLS listener opens would leave a window in which every tenant domain has none.
//
// Only for the deploy that performs the cutover, which is the one that mounts Caddy's data
// directory read-only. Every deploy after it finds no path and does nothing. See
// CaddyCertificateImporter for why skipping this makes the cutover an outage.
if (app.Configuration["Edge:Certificates:ImportFromCaddyPath"] is { Length: > 0 } caddyDataPath)
{
    await app.Services.GetRequiredService<CaddyCertificateImporter>()
        .ImportAsync(caddyDataPath, CancellationToken.None);
}

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
