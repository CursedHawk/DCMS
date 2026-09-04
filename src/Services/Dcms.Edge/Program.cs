using Dcms.Edge;
using Dcms.Edge.Routing;
using Dcms.Edge.Transforms;
using Dcms.Shared.Hosting;
using Dcms.Shared.Messaging;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("edge");
builder.Services.AddDcmsMessaging(builder.Configuration);

builder.Services.Configure<EdgeOptions>(builder.Configuration.GetSection(EdgeOptions.SectionName));
builder.Services.AddSingleton<EdgeConfigProvider>();
builder.Services.AddSingleton<IProxyConfigProvider>(sp => sp.GetRequiredService<EdgeConfigProvider>());
builder.Services.AddHostedService<EdgeConfigInvalidator>();

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

app.MapReverseProxy(proxyPipeline =>
{
    // Inside the proxy pipeline because it needs the matched route: which headers a caller may
    // set depends on whether this request came from an operator on one of our hosts or an
    // anonymous visitor on a tenant's domain.
    proxyPipeline.UsePublicPlaneHeaderScrubbing();
});

app.Run();

public partial class Program;
