using Dcms.Edge;
using Dcms.Edge.Certificates;
using Dcms.Edge.Routing;
using Dcms.Edge.Transforms;
using Dcms.Shared.Caching;
using Dcms.Shared.Data.Edge;
using Dcms.Shared.Hosting;
using Dcms.Shared.Messaging;
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
builder.Services.AddHostedService<CertificateRenewalService>();
builder.Services.AddHostedService<DomainCertificateProvisioner>();

// Opens the TLS listener when Edge:Certificates:TlsEnabled is set. Registered as an options
// configurator rather than configured inline, because the SNI callback needs the container and
// the container does not exist yet when ConfigureKestrel runs.
builder.Services.AddSingleton<IConfigureOptions<KestrelServerOptions>, EdgeTlsConfigurator>();

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
});

var app = builder.Build();

// First in the pipeline, so an exception anywhere below it becomes a ProblemDetails carrying
// the trace id instead of a bare Kestrel 500 with no body and nothing to quote.
app.UseDcmsProblemDetails();

// Before routing, and before anything can read a request header: the edge is the trust
// boundary, and everything behind it trusts its caller completely.
app.UseUntrustedHeaderScrubbing();

app.MapDcmsDefaultEndpoints();

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
