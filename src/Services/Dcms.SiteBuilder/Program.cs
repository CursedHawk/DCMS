using Dcms.SiteBuilder;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Data.Sites;
using Dcms.Shared.Hosting;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("site-builder");
builder.Services.AddDcmsMessaging(builder.Configuration);
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
app.MapDcmsDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "site-builder" }));
app.Run();

public partial class Program;
