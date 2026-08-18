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
