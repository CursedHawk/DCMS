using Dcms.Shared.Data.Audit;
using Dcms.SiteHost;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Data.Tenancy;
using Dcms.Shared.Hosting;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("site-host");
builder.Services.AddDcmsMessaging(builder.Configuration);
builder.Services.AddDcmsObjectStorage(builder.Configuration);
builder.Services.AddMemoryCache();
builder.Services.AddHttpForwarder();

// Reads tenancy + sites with no ambient tenant (resolution is by Host header).
builder.Services.AddDcmsTenancyData(builder.Configuration);
builder.Services.AddDcmsSitesData(builder.Configuration);
builder.Services.AddDcmsAuditData(builder.Configuration);
builder.Services.AddNullTenantContext();
builder.Services.AddSingleton<DomainResolver>();
builder.Services.AddHostedService<SiteCacheInvalidator>();

var contentApi = builder.Configuration["Services:ContentApi"] ?? "http://localhost:5003";

var app = builder.Build();
// First in the pipeline, so an exception anywhere below it becomes a ProblemDetails
// carrying the trace id instead of a bare Kestrel 500 with no body and nothing to quote.
app.UseDcmsProblemDetails();

app.UseDcmsSecurityHeaders();
app.MapDcmsDefaultEndpoints();

// Caddy on-demand TLS authorization: 200 only for verified+linked domains, so
// certificates are never issued for hostnames we don't actually host.
app.MapGet("/internal/tls-allowed", async (string? domain, DomainResolver resolver, CancellationToken ct) =>
    !string.IsNullOrWhiteSpace(domain) && await resolver.IsTlsAllowedAsync(domain, ct)
        ? Results.Ok()
        : Results.NotFound());

app.MapApiProxy(contentApi);   // /api, /hub → content-api (must precede the catch-all)
app.MapSiteHost();             // everything else → static artifacts
app.Run();

public partial class Program;
