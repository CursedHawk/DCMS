using Dcms.Shared.Data.Audit;
using Dcms.Shared.Audit;
using Dcms.MediaWorker;
using Dcms.Shared.Data.Media;
using Dcms.Shared.Hosting;
using Dcms.Shared.Media;
using Dcms.Shared.Messaging;
using Dcms.Shared.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.AddDcmsServiceDefaults("media-worker", AuditProfile.Consumer);
builder.Services.AddDcmsMessaging(builder.Configuration);
builder.Services.AddDcmsObjectStorage(builder.Configuration);

// Reads/writes the media schema with no ambient tenant (scoped per job).
builder.Services.AddDcmsMediaData(builder.Configuration);
// Reaches the audit schema on the shared connection, so it gets the strong path: its records
// commit in the same transaction as the variants it writes.
builder.Services.AddDcmsAuditData(builder.Configuration);
builder.Services.AddNullTenantContext();
builder.Services.AddSingleton<WebpLadderGenerator>();
builder.Services.AddSingleton<VideoTranscoder>();
builder.Services.AddSingleton<AudioTranscoder>();
builder.Services.AddHostedService<ImageProcessingConsumer>();
builder.Services.AddHostedService<VideoProcessingConsumer>();
builder.Services.AddHostedService<AudioProcessingConsumer>();

var app = builder.Build();
// First in the pipeline, so an exception anywhere below it becomes a ProblemDetails
// carrying the trace id instead of a bare Kestrel 500 with no body and nothing to quote.
app.UseDcmsProblemDetails();

app.MapDcmsDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { service = "media-worker" }));
app.Run();

public partial class Program;
