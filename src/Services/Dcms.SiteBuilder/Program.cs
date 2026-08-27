using Dcms.Shared.Audit;
using Dcms.SiteBuilder;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Hosting;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("site-builder", AuditProfile.Consumer);
builder.Services.AddDcmsMessaging(builder.Configuration);
// Connects as dcms_sitebuilder, which has USAGE on "sites" and nothing else — the audit
// schema is deliberately not reachable from here, so records go over JetStream.
builder.Services.AddDcmsAuditOverNats();
builder.Services.AddDcmsObjectStorage(builder.Configuration);

// Reads/writes the sites schema with no ambient tenant (scoped per job).
//
// The *only* schema this service can reach: it connects as a least-privilege role
// with USAGE on `sites` alone. It briefly also registered CmsDbContext, to ask
// whether the tenant had analytics enabled — a query that failed with 42501 on
// every publish. That answer now travels on the publish message, computed by
// admin-api, which already holds the permission. Anything else this service needs
// to know about a tenant belongs on the message too, not in a new DbContext.
builder.Services.AddDcmsSitesData(builder.Configuration);
builder.Services.AddNullTenantContext();
builder.Services.AddSingleton<StaticSiteAssembler>();
builder.Services.AddSingleton<ReactAppBuilder>();
builder.Services.AddHostedService<SitePublishConsumer>();

var app = builder.Build();
// First in the pipeline, so an exception anywhere below it becomes a ProblemDetails
// carrying the trace id instead of a bare Kestrel 500 with no body and nothing to quote.
app.UseDcmsProblemDetails();

app.MapDcmsDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "site-builder" }));
app.Run();

public partial class Program;
